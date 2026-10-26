"""Read-only G8.1 client. This deliberately does not change the Agent TCP protocol."""
import argparse
import json
import socket


def query(port=18779):
    with socket.create_connection(("127.0.0.1", port), timeout=6) as connection:
        connection.settimeout(6)
        with connection.makefile("rwb") as stream:
            def request(value):
                stream.write((json.dumps(value) + "\n").encode("utf-8"))
                stream.flush()
                line = stream.readline(1_048_576)
                if not line.endswith(b"\n"):
                    raise RuntimeError("AI IPC disconnected or exceeded response limit")
                response = json.loads(line)
                if not response.get("ok"):
                    raise RuntimeError(response.get("error", "invalid_response"))
                return response

            binding = request({"v": 1, "id": "bind-1", "op": "bind"})["binding"]
            return request({"v": 1, "id": "state-1", "op": "state", "binding": binding})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18779)
    print(json.dumps(query(parser.parse_args().port), ensure_ascii=False, indent=2))
