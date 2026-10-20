"""Small cancellable OpenAI-compatible Chat Completions client."""
import os
from pathlib import Path
from urllib.parse import urlsplit

import httpx


class LLMError(Exception):
    pass


def load_config(path=None):
    """Read the three literal LLM settings; environment variables take precedence."""
    path = Path(path) if path is not None else Path(__file__).resolve().parents[1] / ".env"
    keys = ("LLM_BASE_URL", "LLM_API_KEY", "LLM_MODEL")
    values = {}
    if path.exists():
        for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            key, separator, value = line.partition("=")
            key, value = key.strip(), value.strip()
            if key not in keys:
                continue
            if not separator:
                raise LLMError(f"Invalid .env assignment on line {number}")
            if value.startswith(("'", '"')):
                if len(value) < 2 or value[-1] != value[0]:
                    raise LLMError(f"Unclosed .env quote on line {number}")
                value = value[1:-1]
            values[key] = value
    values.update({key: os.environ[key] for key in keys if key in os.environ})
    return values


class LLMClient:
    def __init__(self, base_url=None, api_key=None, model=None, timeout=30, transport=None):
        config = load_config()
        base_url = base_url or config.get("LLM_BASE_URL", "")
        api_key = api_key or config.get("LLM_API_KEY", "")
        self.model = model or config.get("LLM_MODEL", "")
        url = urlsplit(base_url)
        if (url.scheme not in ("http", "https") or not url.netloc or url.username
                or url.password or url.query or url.fragment or not api_key or not self.model):
            raise LLMError("Set LLM_BASE_URL, LLM_API_KEY and LLM_MODEL in .env or environment variables")
        self.url = base_url.rstrip("/") + "/chat/completions"
        self.http = httpx.AsyncClient(timeout=timeout, transport=transport,
                                     headers={"Authorization": f"Bearer {api_key}"})

    async def complete(self, messages, tools):
        try:
            response = await self.http.post(self.url, json={"model": self.model,
                "messages": messages, "tools": tools, "tool_choice": "auto", "max_tokens": 512,
                # DeepSeek defaults to thinking; disable it so tool history needs no reasoning_content.
                # This is a raw HTTP body, not the OpenAI SDK's extra_body wrapper.
                "thinking": {"type": "disabled"}})
            response.raise_for_status()
        except httpx.TimeoutException as error:
            raise LLMError("LLM request timed out") from error
        except httpx.HTTPStatusError as error:
            raise LLMError(f"LLM HTTP {error.response.status_code}") from error
        except httpx.RequestError as error:
            raise LLMError(f"LLM network failure ({type(error).__name__})") from error
        try:
            choice = response.json()["choices"][0]
            message = choice["message"]
            if (message["role"] != "assistant" or choice["finish_reason"] not in ("stop", "tool_calls")
                    or not (message.get("tool_calls") or isinstance(message.get("content"), str)
                            and message["content"].strip())):
                raise ValueError()
            return {key: message[key] for key in ("role", "content", "tool_calls") if key in message}
        except (ValueError, KeyError, IndexError, TypeError, AttributeError) as error:
            raise LLMError("Malformed or incomplete LLM response") from error

    async def close(self):
        await self.http.aclose()
