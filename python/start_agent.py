"""CLI entry point. Windows spawn replays this file in every Lua child."""

if __name__ == "__main__":
    from agent import main

    raise SystemExit(main())
