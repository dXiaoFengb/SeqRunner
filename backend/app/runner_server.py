from __future__ import annotations

import argparse
import asyncio
import json
import socket
from pathlib import Path

import uvicorn

from .main import create_app


def ensure_websocket_support() -> None:
    """Fail before publishing readiness when the required protocol is absent."""
    try:
        import websockets  # noqa: F401
        from uvicorn.protocols.websockets import websockets_impl  # noqa: F401
    except ImportError as error:
        raise RuntimeError(
            "缺少实时通信组件 websockets，无法启动本地实时连接。请重新安装后端依赖。"
        ) from error


async def serve(data_directory: Path, instance_id: str) -> None:
    ensure_websocket_support()
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    port = listener.getsockname()[1]
    application = create_app(data_directory, instance_id=instance_id)
    print(f"RUNNER_READY {json.dumps({'port': port, 'instanceId': instance_id})}", flush=True)
    server = uvicorn.Server(
        uvicorn.Config(application, log_level="warning", access_log=False, ws="websockets")
    )
    await server.serve(sockets=[listener])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", required=True)
    parser.add_argument("--instance-id", required=True)
    args = parser.parse_args()
    asyncio.run(serve(Path(args.data_dir), args.instance_id))


if __name__ == "__main__":
    main()
