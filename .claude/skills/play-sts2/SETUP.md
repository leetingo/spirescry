# First-time setup

You need the user's own Slay the Spire 2 install (its files are read
from disk, never distributed), .NET 9, Rust, and python3. macOS finds
the game automatically; elsewhere set `STS2_GAME_DIR` to the game's
binary directory first.

```sh
./build.sh headless-setup    # build the windowless host from the install
./build.sh cli deploy-cli    # build the spirescry command → ~/.local/bin
```

Done when `spirescry --version` prints. Still `command not found` → put
`~/.local/bin` on PATH, or point `SPIRESCRY_CLI_BIN` at a directory
that is.
