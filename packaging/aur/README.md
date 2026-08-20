# AUR packaging

`cxagent-bin` on the [AUR](https://aur.archlinux.org/packages/cxagent-bin) — the released Linux
binary, repackaged. No .NET SDK is needed to install it.

```bash
yay -S cxagent-bin        # or paru, or makepkg -si
```

## Why `-bin` rather than a source package

The release already publishes self-contained `linux-x64` and `linux-arm64` binaries with a signed
`SHA256SUMS`. A source package would pull a ~1 GB `dotnet-sdk` and rebuild what is already built and
tested — and cxagent targets `net10.0`, which the AUR's `dotnet-sdk` may not yet provide.

## `depends=('icu')` is not optional

The binary carries its own .NET runtime but **dlopens `libicuuc.so` and `libicui18n.so`** at startup
for globalization. `ldd` shows only glibc and libstdc++, so the dependency is invisible to the usual
check and a missing `icu` fails at RUN time with an unhelpful globalization error rather than at
install time. Verified against the published v0.4.0 binary.

`options=('!strip')` matters for the same class of reason: the file is a single-file .NET bundle, and
stripping it corrupts the embedded host.

## How releases reach the AUR

The `aur` job in `.github/workflows/release.yml` regenerates these two files on each tag and pushes
them. The checksums come from the release's own `SHA256SUMS` — the file users are told to verify
against — so the package and the published checksums cannot disagree.

## One-time setup

The AUR has no API for creating a package; the first push creates it.

1. Clone and push by hand once:
   ```bash
   git clone ssh://aur@aur.archlinux.org/cxagent-bin.git
   cp packaging/aur/PKGBUILD packaging/aur/.SRCINFO cxagent-bin/
   cd cxagent-bin && git add -A && git commit -m "cxagent-bin 0.4.0" && git push
   ```
2. Add an SSH key to your AUR account (Account → SSH Public Key), then store the **private** half as
   the repository secret `AUR_SSH_KEY`.

Until `AUR_SSH_KEY` exists the job skips with a message rather than failing, so a release is never
blocked by AUR setup.

## Updating by hand

Change `pkgver` and the three `sha256sums` in both files, keeping them identical. `.SRCINFO` is
normally produced by `makepkg --printsrcinfo > .SRCINFO`; it is committed here so the CI job can edit
it without an Arch machine.
