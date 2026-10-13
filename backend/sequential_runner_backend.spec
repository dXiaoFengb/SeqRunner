# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.building.build_main import Analysis, EXE, PYZ
from PyInstaller.utils.hooks import collect_submodules

analysis = Analysis(
    ["runner_backend.py"],
    pathex=["."],
    hiddenimports=[
        "uvicorn.logging",
        "uvicorn.loops.auto",
        "uvicorn.protocols.http.auto",
        "uvicorn.protocols.websockets.auto",
        "uvicorn.protocols.websockets.websockets_impl",
        *collect_submodules("websockets"),
    ],
)
pyz = PYZ(analysis.pure)
exe = EXE(pyz, analysis.scripts, analysis.binaries, analysis.datas, name="sequential-runner-backend", console=False)
