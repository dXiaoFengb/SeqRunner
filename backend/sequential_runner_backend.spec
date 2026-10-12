# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.building.build_main import Analysis, EXE, PYZ

analysis = Analysis(
    ["runner_backend.py"],
    pathex=["."],
    hiddenimports=["uvicorn.logging", "uvicorn.loops.auto", "uvicorn.protocols.http.auto", "uvicorn.protocols.websockets.auto"],
)
pyz = PYZ(analysis.pure)
exe = EXE(pyz, analysis.scripts, analysis.binaries, analysis.datas, name="sequential-runner-backend", console=False)
