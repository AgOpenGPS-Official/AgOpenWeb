# Linux Environment Setup

Guide for setting up the AgOpenWeb development environment on Linux.

## Prerequisites

- **OS**: Debian 11+, Ubuntu 20.04+, Fedora 38+, or similar
- **Architecture**: x64 (ARM64 also supported)
- **Disk space**: ~1 GB for .NET SDK + NuGet packages

## 1. Install .NET 10.0 SDK

### Option A: Microsoft install script (recommended, no root required)

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0
```

Add to your shell profile (`~/.bashrc`, `~/.zshrc`, etc.):

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

Then reload:

```bash
source ~/.bashrc  # or source ~/.zshrc
```

### Option B: Package manager

**Debian/Ubuntu:**

```bash
# Add Microsoft package repository
wget https://packages.microsoft.com/config/debian/11/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0
```

**Fedora:**

```bash
sudo dnf install dotnet-sdk-10.0
```

### Verify installation

```bash
dotnet --version
# Should output 10.0.x
```

## 2. Install system dependencies

The headless daemon needs only ICU (.NET globalization). The desktop launcher window
(`--launcher`) is Photino.NET over WebKitGTK and needs it installed.

**Debian/Ubuntu:**

```bash
sudo apt-get install -y libicu-dev                       # daemon
sudo apt-get install -y libwebkit2gtk-4.1-0 libsoup-3.0-0  # launcher window
```

**Fedora:**

```bash
sudo dnf install -y libicu                               # daemon
sudo dnf install -y webkit2gtk4.1 libsoup3                # launcher window
```

## 3. Clone and build

```bash
git clone <repository-url> AgOpenWeb
cd AgOpenWeb

# Restore NuGet packages and build
dotnet build AgOpenWeb.sln
```

## 4. Run the application

```bash
# Headless daemon (the Linux default): the UI is at http://localhost:5174
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
# Desktop window (real hardware; needs WebKitGTK)
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj -- --launcher
```

## 5. Run tests

```bash
dotnet run --project TestRunner/TestRunner.csproj
```

## Troubleshooting

### `dotnet: command not found` after install script

The install script places the SDK in `~/.dotnet`. Ensure your PATH is updated:

```bash
export PATH="$HOME/.dotnet:$PATH"
```

Add this to your shell profile to make it permanent.

### No display (CI, SSH)

Run the daemon (the default on Linux, or `--headless`) and open `http://<host>:5174` from a
browser on another machine. Only `--launcher` needs a display.

### NuGet restore failures

If behind a corporate proxy:

```bash
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
```

### ICU / globalization errors

If you see `System.Globalization.CultureNotFoundException`:

```bash
sudo apt-get install -y libicu-dev   # Debian/Ubuntu
sudo dnf install -y libicu-devel     # Fedora
```

Or use invariant globalization:

```bash
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
```
