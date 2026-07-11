<p align="center">
  <a href="README.md"><img alt="中文" src=".github/badges/language-zh.svg"></a>
  <a href="README_en.md"><img alt="English" src=".github/badges/language-en.svg"></a>
  <a href="CHANGELOG_en.md"><img alt="Changelog" src=".github/badges/changelog-en.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/releases"><img alt="Releases" src=".github/badges/releases.svg"></a>
<!-- code-stats:start -->
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="C# lines" src=".github/badges/code-lines-csharp.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="JSON lines" src=".github/badges/code-lines-json.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="YAML lines" src=".github/badges/code-lines-yaml.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="MSBuild script lines" src=".github/badges/code-lines-msbuild-script.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="Total code lines" src=".github/badges/code-lines-total.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="Total added lines" src=".github/badges/code-lines-added.svg"></a>
  <a href="https://github.com/JMC-Mods/SlayTheSpire2_QuickSL/actions/workflows/code-lines.yml"><img alt="Total deleted lines" src=".github/badges/code-lines-deleted.svg"></a>
<!-- code-stats:end -->
</p>
# QuickSL

## Installation

Subscribe on Steam Workshop, or download a release and extract it into the game's `mods` directory.

QuickSL requires [JmcModLib](https://github.com/JMC-Mods/SlayTheSpire2_JmcModLib/releases) `1.6.1` or newer.

```text
Slay the Spire 2/
└── mods/
    ├── JmcModLib/
    └── QuickSL/
        ├── QuickSL.dll
        ├── QuickSL.pck
        └── QuickSL.json
```

## Features

QuickSL reloads the current run from its save through a configurable keyboard or controller shortcut and a pause-menu entry. Its result is equivalent to saving and quitting, then selecting Continue, while skipping the main-menu interaction.

- The default keyboard shortcut is `F5`.
- Fast mode can skip reload fade transitions.
- Single-player Quick Save Load is always available.
- Multiplayer Quick Save Load is optional and disabled by default.
- A host or client can request a synchronized multiplayer reload when the feature is enabled.
- The host can control client-initiated requests and confirmation behavior.

## Optional multiplayer protocol

While multiplayer Quick Save Load is disabled, QuickSL does not register its custom network messages and does not affect multiplayer mod synchronization. This lets players keep QuickSL installed for its single-player feature without requiring other lobby members to install it.

When multiplayer Quick Save Load is enabled, every player in the session must install compatible versions of QuickSL and JmcModLib. JmcModLib applies the protocol change immediately while no network activity exists. A change made during hosting, joining, or an active session remains pending until the game has fully disconnected. JmcModLib falls back to its existing restart confirmation only if safe hot application fails. If the feature is disabled in a multiplayer run, invoking QuickSL displays a disabled message and never falls back to a single-player reload.

## Multiplayer flow

1. The host or a client invokes QuickSL.
2. A client request is validated by the host according to its settings.
3. Required host and client confirmations are collected.
4. The host sends the authoritative multiplayer run save to connected clients.
5. Every player reloads the same run in a coordinated sequence.
6. A rejection, timeout, invalid state, or transfer error cancels the operation.

The current implementation transfers an uncompressed JSON save with a `1 MiB` limit.

## Compatibility

Slay the Spire 2 is in Early Access, so game updates may require a QuickSL update.

[GitHub repository](https://github.com/JMC-Mods/SlayTheSpire2_QuickSL)
