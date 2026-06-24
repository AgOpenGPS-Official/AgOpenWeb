# Cross-Platform Migration Plan

## Decision: Fresh Start in AgOpenWeb3

Instead of refactoring the tangled AgOpenWeb2 project in place, we'll create a clean new project `AgOpenWeb3` that:
1. Takes only the working, known-good code from AgOpenWeb2
2. Has NO reference to SourceCode/AgOpenGPS.Core
3. Is designed from the start for 95%/5% cross-platform split

### Why Fresh Start?
- AgOpenWeb2 has tangled references to SourceCode/AgOpenGPS.Core
- Failed iOS attempt left confusing state
- Clean break is safer and easier to reason about
- Can always reference AgOpenWeb2 for working code

---

## What We're Taking from AgOpenWeb2 (Known Working)

### From AgOpenWeb.Models/ (models that work)
- GpsData.cs, Position.cs
- VehicleConfiguration.cs, Vehicle.cs
- BackgroundImage.cs
- Other domain models (no AgOpenGPS.Core dependencies)

### From AgOpenWeb.Services/ (services that work WITHOUT AgOpenGPS.Core refs)
- UdpCommunicationService.cs + IUdpCommunicationService.cs
- NtripClientService.cs + INtripClientService.cs
- NmeaParserService.cs
- SettingsService.cs + ISettingsService.cs
- FieldService.cs + IFieldService.cs
- GuidanceService.cs + IGuidanceService.cs
- BoundaryRecordingService.cs + IBoundaryRecordingService.cs
- BoundaryFileService.cs, FieldPlaneFileService.cs, BackgroundImageFileService.cs
- DisplaySettingsService.cs + IDisplaySettingsService.cs

### From AgOpenWeb.ViewModels/
- MainViewModel.cs (bindings for GPS, modules, etc.)

### From AgOpenWeb.Desktop/ (platform-specific)
- Views/MainWindow.axaml + .cs
- Views/DataIODialog.axaml + .cs
- Controls/OpenGLMapControl.cs
- Converters/
- DependencyInjection/ServiceCollectionExtensions.cs (will need cleanup)
- Assets/

---

## New Project Structure: AgOpenWeb3

```
/Users/chris/Code/AgOpenWeb3/
├── AgOpenWeb.sln
├── CLAUDE.md
│
├── Shared/ (95% - cross-platform)
│   ├── AgOpenWeb.Models/        # Data models only
│   ├── AgOpenWeb.Services/      # Business logic services
│   └── AgOpenWeb.ViewModels/    # MVVM ViewModels
│
└── Platforms/ (5% - platform-specific)
    ├── AgOpenWeb.Desktop/       # Windows/macOS/Linux desktop
    │   ├── App.axaml + .cs
    │   ├── Views/
    │   ├── Controls/OpenGLMapControl.cs
    │   ├── Converters/
    │   └── DependencyInjection/
    │
    └── AgOpenWeb.iOS/           # iOS
        ├── App.axaml + .cs
        ├── Views/iOSMainView.axaml + .cs
        ├── Controls/ (SkiaMapControl future)
        ├── Converters/
        └── DependencyInjection/
```

---

## Implementation Phases

### Phase 1: Create Project Structure
```bash
mkdir -p /Users/chris/Code/AgOpenWeb3
cd /Users/chris/Code/AgOpenWeb3

# Create shared projects
mkdir -p Shared/AgOpenWeb.Models
mkdir -p Shared/AgOpenWeb.Services/Interfaces
mkdir -p Shared/AgOpenWeb.ViewModels

# Create platform projects
mkdir -p Platforms/AgOpenWeb.Desktop/Views
mkdir -p Platforms/AgOpenWeb.Desktop/Controls
mkdir -p Platforms/AgOpenWeb.Desktop/Converters
mkdir -p Platforms/AgOpenWeb.Desktop/DependencyInjection
mkdir -p Platforms/AgOpenWeb.Desktop/Assets

mkdir -p Platforms/AgOpenWeb.iOS/Views
mkdir -p Platforms/AgOpenWeb.iOS/Converters
mkdir -p Platforms/AgOpenWeb.iOS/DependencyInjection
```

### Phase 2: Create .csproj Files (No SourceCode References!)

**Shared/AgOpenWeb.Models/AgOpenWeb.Models.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

**Shared/AgOpenWeb.Services/AgOpenWeb.Services.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\AgOpenWeb.Models\AgOpenWeb.Models.csproj" />
  </ItemGroup>
</Project>
```

**Shared/AgOpenWeb.ViewModels/AgOpenWeb.ViewModels.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ReactiveUI" Version="20.1.1" />
    <PackageReference Include="ReactiveUI.Fody" Version="19.5.41" />
    <ProjectReference Include="..\AgOpenWeb.Models\AgOpenWeb.Models.csproj" />
    <ProjectReference Include="..\AgOpenWeb.Services\AgOpenWeb.Services.csproj" />
  </ItemGroup>
</Project>
```

**Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.9" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.9" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.9" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.3.6" />
    <PackageReference Include="ReactiveUI" Version="20.1.1" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
    <PackageReference Include="Silk.NET.OpenGL" Version="2.22.0" />
    <PackageReference Include="StbImageSharp" Version="2.30.15" />
    <PackageReference Include="System.IO.Ports" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Shared\AgOpenWeb.Models\AgOpenWeb.Models.csproj" />
    <ProjectReference Include="..\..\Shared\AgOpenWeb.Services\AgOpenWeb.Services.csproj" />
    <ProjectReference Include="..\..\Shared\AgOpenWeb.ViewModels\AgOpenWeb.ViewModels.csproj" />
  </ItemGroup>
</Project>
```

**Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-ios</TargetFramework>
    <RuntimeIdentifier>iossimulator-arm64</RuntimeIdentifier>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.9" />
    <PackageReference Include="Avalonia.iOS" Version="11.3.9" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.9" />
    <PackageReference Include="ReactiveUI" Version="20.1.1" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Shared\AgOpenWeb.Models\AgOpenWeb.Models.csproj" />
    <ProjectReference Include="..\..\Shared\AgOpenWeb.Services\AgOpenWeb.Services.csproj" />
    <ProjectReference Include="..\..\Shared\AgOpenWeb.ViewModels\AgOpenWeb.ViewModels.csproj" />
  </ItemGroup>
</Project>
```

### Phase 3: Copy Models (Clean, No Dependencies)
Copy from AgOpenWeb2/AgOpenWeb/AgOpenWeb.Models/ to AgOpenWeb3/Shared/AgOpenWeb.Models/

Files to copy:
- GpsData.cs
- Position.cs
- VehicleConfiguration.cs
- Vehicle.cs
- BackgroundImage.cs
- Other models that don't reference AgOpenGPS.Core

### Phase 4: Copy Services (Remove AgOpenGPS.Core References)
Copy from AgOpenWeb2/AgOpenWeb/AgOpenWeb.Services/ to AgOpenWeb3/Shared/AgOpenWeb.Services/

Key services needed for working desktop app:
- Interfaces/IUdpCommunicationService.cs
- Interfaces/INtripClientService.cs
- Interfaces/ISettingsService.cs
- Interfaces/IGuidanceService.cs
- Interfaces/IFieldService.cs
- Interfaces/IBoundaryRecordingService.cs
- Interfaces/IDisplaySettingsService.cs
- UdpCommunicationService.cs
- NtripClientService.cs
- NmeaParserService.cs
- SettingsService.cs
- GuidanceService.cs
- FieldService.cs
- BoundaryRecordingService.cs
- DisplaySettingsService.cs

**Important**: Remove any `using AgOpenGPS.Core.*` statements and ensure all types come from AgOpenWeb.Models

### Phase 5: Copy ViewModels
Copy MainViewModel.cs from AgOpenWeb2 to AgOpenWeb3/Shared/AgOpenWeb.ViewModels/

Update imports to use AgOpenWeb.Services.Interfaces (not AgOpenGPS.Core)

### Phase 6: Copy Desktop Platform Code
Copy from AgOpenWeb2/AgOpenWeb/AgOpenWeb.Desktop/:
- Program.cs
- App.axaml + App.axaml.cs
- Views/MainWindow.axaml + .cs
- Views/DataIODialog.axaml + .cs
- Controls/OpenGLMapControl.cs
- Converters/*.cs
- Assets/

Update DependencyInjection/ServiceCollectionExtensions.cs to:
- Remove all AgOpenGPS.Core references
- Use AgOpenWeb.Services.Interfaces for all services

### Phase 7: Build and Test Desktop
```bash
cd /Users/chris/Code/AgOpenWeb3
dotnet build Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
dotnet run --project Platforms/AgOpenWeb.Desktop/AgOpenWeb.Desktop.csproj
```

Verify:
- [ ] App launches
- [ ] GPS status displays
- [ ] Module status displays
- [ ] NTRIP dialog works
- [ ] Map renders with vehicle

### Phase 8: Create iOS Platform Code
Create minimal iOS entry point:
- Program.cs (iOS main)
- App.axaml + App.axaml.cs (ISingleViewApplicationLifetime)
- Views/iOSMainView.axaml + .cs (simplified UI)
- Converters/ (copy from Desktop)
- DependencyInjection/ServiceCollectionExtensions.cs (same as Desktop but iOS namespace)

### Phase 9: Build and Test iOS
```bash
cd /Users/chris/Code/AgOpenWeb3
dotnet build Platforms/AgOpenWeb.iOS/AgOpenWeb.iOS.csproj

# Install on simulator
open -a Simulator
xcrun simctl install booted bin/Debug/net10.0-ios/iossimulator-arm64/AgOpenWeb.iOS.app
xcrun simctl launch booted com.agopengps.agvaloniagps
```

Verify:
- [ ] App launches on simulator
- [ ] UI displays correctly
- [ ] Bindings work

### Phase 10: Commit and Document
- Create git repo
- Write CLAUDE.md
- Commit clean working state

---

## Key Rules for AgOpenWeb3

1. **NO references to SourceCode/AgOpenGPS.Core** - ever
2. **Shared/ projects have NO Avalonia dependencies** - pure .NET
3. **Platform/ projects contain ALL UI code** - Views, Controls, Converters
4. **Services use interfaces** - platform projects wire up DI
5. **ViewModels use ReactiveUI** - shared across platforms

---

## Rollback Plan

AgOpenWeb2 remains untouched at:
- `/Users/chris/Code/AgOpenWeb2/`
- Branch: feature/skiasharp-mobile
- Commit: ede90f8

If AgOpenWeb3 fails, we still have the original working Desktop app in AgOpenWeb2.
