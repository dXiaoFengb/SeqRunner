import sys

if __name__ == "__main__":
    if sys.argv[1:] == ["--uia-helper"]:
        from app.startup_uia_native import main
    else:
        from app.runner_server import main
    main()
