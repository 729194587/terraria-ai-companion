"""Small cancellable OpenAI-compatible Chat Completions client."""
import os
import asyncio
import itertools
import time
from pathlib import Path
from urllib.parse import urlsplit

import httpx
import agent_trace as tracing
import cost_metrics


class LLMError(Exception):
    def __init__(self, message, *, retryable=False, context_overflow=False):
        super().__init__(message)
        self.retryable = retryable
        self.context_overflow = context_overflow


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
    def __init__(self, base_url=None, api_key=None, model=None, timeout=30, transport=None, max_tokens=2048):
        if type(max_tokens) is not int or not 1 <= max_tokens <= 8192:
            raise ValueError('max_tokens must be an integer in 1..8192')
        self.max_tokens = max_tokens
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
        self._request_ids = itertools.count(1)
        tracing.trace.add_secret(api_key)

    async def complete(self, messages, tools):
        request_id = next(self._request_ids)
        started = time.monotonic()
        accounting = {}
        outcome = "completed"
        try:
            return await self._complete(messages, tools, request_id, started, accounting)
        except asyncio.CancelledError:
            outcome = "cancelled"
            raise
        except Exception:
            outcome = "failed"
            raise
        finally:
            cost_metrics.record_usage(accounting.get("usage"), request_id,
                                      (time.monotonic() - started) * 1000, outcome)

    async def _complete(self, messages, tools, request_id, started, accounting):
        body = {"model": self.model,
                "messages": messages, "tools": tools, "tool_choice": "auto", "max_tokens": self.max_tokens,
                # Raw HTTP body, not the OpenAI SDK's extra_body wrapper.
                "thinking": {"type": "disabled"}}
        tracing.emit_full("llm_request", model_request_id=request_id, model=self.model, body=body)
        try:
            response = await self.http.post(self.url, json=body)
            response.raise_for_status()
        except asyncio.CancelledError:
            tracing.emit_full("llm_cancelled", model_request_id=request_id,
                               elapsed_ms=(time.monotonic() - started) * 1000)
            raise
        except httpx.TimeoutException as error:
            tracing.emit_full("llm_error", model_request_id=request_id, category="timeout",
                               elapsed_ms=(time.monotonic() - started) * 1000)
            raise LLMError("LLM request timed out", retryable=True) from error
        except httpx.HTTPStatusError as error:
            tracing.emit_full("llm_error", model_request_id=request_id, category="http",
                               status_code=error.response.status_code,
                               elapsed_ms=(time.monotonic() - started) * 1000)
            status = error.response.status_code
            # Read provider error only to classify it; never expose response bodies/secrets.
            try:
                detail = error.response.json().get('error', {})
                code = detail.get('code', '') if isinstance(detail, dict) else ''
                message = detail.get('message', '') if isinstance(detail, dict) else ''
            except (ValueError, AttributeError):
                code = message = ''
            overflow = status in (400, 413, 422) and (
                code in ('context_length_exceeded', 'context_window_exceeded', 'max_context_length')
                or 'maximum context length' in str(message).lower()
                or 'context window' in str(message).lower())
            raise LLMError(f"LLM HTTP {status}", context_overflow=overflow,
                           retryable=status in (408, 429, 500, 502, 503, 504)) from error
        except httpx.RequestError as error:
            tracing.emit_full("llm_error", model_request_id=request_id, category="network",
                               error_type=type(error).__name__, elapsed_ms=(time.monotonic() - started) * 1000)
            raise LLMError(f"LLM network failure ({type(error).__name__})", retryable=True) from error
        try:
            payload = response.json()
            accounting["usage"] = payload.get("usage")
            choice = payload["choices"][0]
            message = choice["message"]
            tracing.emit_full("llm_response", model_request_id=request_id, model=self.model,
                               assistant=message, finish_reason=choice.get("finish_reason"),
                               elapsed_ms=(time.monotonic() - started) * 1000)
            if (message["role"] != "assistant" or choice["finish_reason"] not in ("stop", "tool_calls")
                    or not (message.get("tool_calls") or isinstance(message.get("content"), str)
                            and message["content"].strip())):
                raise ValueError()
            returned = {key: message[key] for key in ("role", "content", "tool_calls") if key in message}
            tracing.emit_full("llm_return", model_request_id=request_id, assistant=returned)
            return returned
        except (ValueError, KeyError, IndexError, TypeError, AttributeError) as error:
            tracing.emit_full("llm_error", model_request_id=request_id, category="invalid_response",
                               error_type=type(error).__name__, elapsed_ms=(time.monotonic() - started) * 1000)
            raise LLMError("Malformed or incomplete LLM response") from error

    async def close(self):
        await self.http.aclose()
