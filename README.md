# DSH MSYS2 agent-profile installer

Installs MSYS2-backed coding-agent profiles for the DeepSeek Harness on
Windows: **one installer, five environments** — mingw64, mingw32, clang64,
clangarm64 and ucrt64.

Each selected environment becomes an agent preset that appears in the DSH
roster, so after installing you just run `npx @deepseek-ai/dsh web` and pick a
profile. There is no custom launcher and no host-environment setup.

## Quick start

```
build.cmd                 # compile installer + tests
build\msys2-installer.exe # open the GUI
```

Then tick the flavours you want and press Install. The GUI finds
`msys2_shell.cmd` automatically (PATH first, then the usual locations); if it
cannot, Browse… opens a file dialog and the selected path is validated
immediately, with the reason shown when it is wrong.

Headless, for scripting:

```
build\msys2-installer.exe --list
build\msys2-installer.exe --install
build\msys2-installer.exe --install --shell-cmd C:\msys64\msys2_shell.cmd --flavour mingw64
```

## What gets installed

Into `%USERPROFILE%\.dsh\.agent-presets\<id>\`:

| File | Purpose |
| --- | --- |
| `agent.cordis.yml` | the agent composition |
| `preset.yml` | picker name and description |
| `msys2_shell_shim.exe` | compiled shell shim (see below) |
| `README.md` | the exact invocation, for hand-reproducing a failure |

Ids are `msys2-mingw64`, `msys2-mingw32`, `msys2-ucrt64`, `msys2-clang64` and
`msys2-clangarm64`. Set `DSH_HOME` to install somewhere other than `~/.dsh`
(useful for testing).

## How a command runs

Every command an agent issues ends up as:

```
msys2_shell.cmd -defterm -no-start -use-full-path -<flavour> -here -c "<command>"
```

- `-no-start` makes the batch file return the login shell's own exit code,
  which is what makes the `[exit code: N]` reporting meaningful.
- `-use-full-path` inherits the Windows PATH rather than MSYS2's trimmed one.
- `-here` makes bash start in the spawn's working directory, so an agent's
  `workdir` reaches it intact.

## Why there is a shim .exe

DSH's `pwsh` executor spawns one **fixed** argv per command:

```
[<pwshPath>, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", <command>]
```

with no shell involved, and `pwshPath` is used verbatim as `argv[0]` of a
direct `CreateProcessW`. Three consequences force a shim:

1. `argv[0]` cannot be a `.cmd`/`.bat` — Node's spawn rejects those with
   `EINVAL`, so `pwshPath` cannot simply be `msys2_shell.cmd`.
2. The command arrives with PowerShell's encoding preamble prepended
   (`[Console]::OutputEncoding = ...`), which bash cannot parse. Left in, *every*
   command fails with ``syntax error near unexpected token `('``.
3. `msys2_shell.cmd` is a batch file, so the command would have to survive both
   cmd.exe's argument parser and bash's. Escaping for both at once is fragile —
   a command containing double quotes comes back mangled.

`msys2_shell_shim.exe` is a real executable that closes all three: it strips the
preamble, writes the command to a temporary `.sh` file, and passes
`msys2_shell.cmd` only a short, simple path with no spaces or metacharacters.
The command bytes therefore never travel through cmd.exe's parser, so no
escaping can corrupt them. Exit codes propagate verbatim.

The shim is compiled from `src/Msys2ShellShim.cs` at install time using
`csc.exe`, which ships with the .NET Framework on every Windows — no SDK and no
network access required.

## Why the presets are unconfined

MSYS2 binaries **cannot start** under the Windows ACL sandbox. `usr\bin` loads
`msys-2.0.dll`, whose startup creates the per-user named section
`CreateFileMapping S-1-5-21-<user>-1001.1`; a write-restricted token denies it
with Win32 error 5, so `bash.exe` terminates before running anything:

```
bash.exe: *** fatal error - CreateFileMapping S-1-5-21-...-1001.1, Win32 error 5.
```

This is not specific to the preset — `msys2_shell.cmd` and `ls.exe` fail the
same way — so each preset pins `mode: danger-full-access`. Treat a session on
one of these profiles as shell access that no file-write boundary mediates.

## Toolchains are a separate, user-owned step

The installer wires the shell, not the compilers. On this machine, for example,
`/mingw32/bin` and `/ucrt64/bin` are empty and `/mingw64/bin` has no `gcc`,
because those pacman packages are not installed; MSYS2's own
`/usr/bin/gcc` (which targets Cygwin) is what resolves. Install the toolchain
for a flavour with, e.g.:

```
pacman -S mingw-w64-x86_64-gcc     # mingw64
pacman -S mingw-w64-ucrt-x86_64-gcc # ucrt64
pacman -S mingw-w64-clang-x86_64-clang  # clang64
```

The test suite reports which toolchains are present so a "compiler not found"
report can be attributed correctly.

## Layout

| Path | Contents |
| --- | --- |
| `src/Msys2Flavour.cs` | the five flavours; `msys2_shell.cmd` discovery and validation; paths |
| `src/PresetWriter.cs` | composition/metadata/README generation; install and uninstall |
| `src/ShimBuilder.cs` | locating `csc.exe` and compiling the shim |
| `src/Msys2ShellShim.cs` | the shim itself |
| `src/InstallerForm.cs` | the WinForms GUI and the headless CLI |
| `tests/` | the C# test suite |

## Tests

```
build\msys2-tests.exe
build\msys2-tests.exe --filter ShimTests
build\msys2-tests.exe --list
```

A self-hosted C# framework (`tests/TestFramework.cs`): `[TestClass]` /
`[TestMethod]` attributes, assertions, reflection-based discovery, per-test
pass/fail and a non-zero exit on failure. MSTest/xUnit/NUnit are not used
because they cannot be restored without a .NET SDK and a NuGet cache, which
this machine does not have.

`tests/ShimTests.cs` **executes real MSYS2 bash** to verify that commands
reach the intended environment, that exit codes propagate and that quoting
survives. Those tests require an unconfined process: under the ACL sandbox
`msys-2.0.dll` cannot start, and each such test reports itself as **skipped**
with that reason rather than passing silently.

`tests/validate-preset.cjs` additionally parses every installed preset with the
same YAML library DSH uses, checking the composition shape that
`dsh-agent-presets` requires.

## Requirements

- Windows
- The .NET Framework (for `csc.exe`) — present on every standard Windows install
- An MSYS2 install, for the profiles to actually run
