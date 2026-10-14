# English

## Aurora Launcher

A personal Minecraft launcher for Windows built with WinUI 3. The UI project is located in `AuroraLauncher.WinUI`, while game services and APIs are in `AuroraLauncher.Core`.

## Building and Running

Requires the .NET 8 SDK and Windows 10 version 1809 or later. From the `AuroraLauncher` directory:

```powershell
dotnet build .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release
dotnet run --project .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release
```

After building, the executable is located at `AuroraLauncher.WinUI\bin\Release\net8.0-windows10.0.19041.0\AuroraLauncher.WinUI.exe`. To publish the application to a folder:

```powershell
dotnet publish .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true
```

## Features

- Official Microsoft sign-in using a one-time code. Passwords are entered on Microsoft's website; tokens are stored using Windows DPAPI.
- Isolated Vanilla, Forge, NeoForge, and Fabric installations, with support for selecting Minecraft versions and available mod loader versions.
- Installation of the game client, libraries, game assets, and a compatible Java runtime. The launcher can use a detected Java installation or perform a deep scan across available drives.
- Launching the selected installation using a Minecraft Java Edition account.
- Mod discovery through Modrinth without an API key and CurseForge with a personal API key. Compatible files can be installed into the `mods` directory, and installed files can be removed.
- In-app configuration of JVM memory allocation, Java path, window size, additional JVM arguments, and API settings.

## Data Storage

By default, data is stored in `%APPDATA%\AuroraLauncher`, including settings, the DPAPI-encrypted account, the installation registry, a shared library and asset cache, Java runtimes, and logs. Each installation maintains its own separate game directory.

CurseForge requires a valid CurseForge API key. Vanilla versions and mod loader installers are downloaded from official servers, while Modrinth uses its official API. Game file downloads are verified against published checksums wherever the server provides them.

## Known Limitations

- CurseForge search is unavailable until an API key is configured. Some CurseForge files do not provide a direct CDN URL and therefore cannot be installed through the API.
- Individual mod installation is supported. Importing CurseForge or Modrinth modpacks is not yet implemented.
- Forge and NeoForge installation depends on the availability of an official installer for the specific Minecraft version.

# Russian

## Aurora Launcher

Персональный лаунчер Minecraft для Windows на WinUI 3. UI-проект находится в `AuroraLauncher.WinUI`, игровые службы и API — в `AuroraLauncher.Core`.

## Сборка и запуск

Нужен .NET 8 SDK и Windows 10 1809 или новее. Из каталога `AuroraLauncher`:

```powershell
dotnet build .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release
dotnet run --project .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release
```

Исполняемый файл после сборки находится в `AuroraLauncher.WinUI\bin\Release\net8.0-windows10.0.19041.0\AuroraLauncher.WinUI.exe`. Для публикации в папку:

```powershell
dotnet publish .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true
```

## Возможности

- Официальный вход Microsoft по одноразовому коду. Пароль вводится на странице Microsoft; токены хранятся через DPAPI Windows.
- Изолированные сборки Vanilla, Forge, NeoForge и Fabric с выбором версии Minecraft и доступной версии загрузчика.
- Установка клиента, библиотек, игровых ресурсов и подходящей Java. Можно использовать найденную Java или выполнить глубокий поиск по дискам.
- Запуск выбранной сборки с аккаунтом Minecraft Java Edition.
- Поиск модов в Modrinth без ключа и CurseForge с личным API-ключом; установка совместимого файла в `mods` и удаление установленных файлов.
- Память JVM, путь к Java, размер окна, дополнительные JVM-аргументы и настройки API в приложении.

## Данные

По умолчанию данные хранятся в `%APPDATA%\AuroraLauncher`: настройки, DPAPI-зашифрованный аккаунт, реестр сборок, кэш общих библиотек/ресурсов, рантаймы и логи. Каждая сборка хранит папку игры отдельно.

CurseForge требует выданный CurseForge API-ключ. Ванильные версии и установщики загрузчиков берутся с официальных серверов; Modrinth использует официальный API. Загрузки игровых файлов проверяются по опубликованным контрольным суммам там, где сервер их предоставляет.

## Известные границы

- Поиск CurseForge недоступен до ввода API-ключа; некоторые файлы CurseForge не дают прямую CDN-ссылку и тогда их нельзя установить через API.
- Поддерживается установка отдельных модов. Импорт модпаков CurseForge/Modrinth пока не реализован.
- Установка Forge/NeoForge зависит от доступности официального установщика для конкретной версии Minecraft.

---

# Legal Notice / Юридическая информация

## English

Aurora Launcher is an independent, unofficial third-party launcher for Minecraft: Java Edition. It is not affiliated with, endorsed by, sponsored by, or approved by Mojang Studios or Microsoft.

**NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**

Minecraft is a trademark of Mojang Studios. All rights to Minecraft, its name, logos, game assets, and related intellectual property belong to their respective owners.

Aurora Launcher is a separate software project and does not include or distribute copies of the Minecraft game. Game files are downloaded from official sources where available. Users must comply with the applicable Minecraft End User License Agreement and other relevant terms when accessing or playing the game.

This project is not sponsored by or affiliated with Modrinth, CurseForge, or Microsoft. Third-party names and trademarks belong to their respective owners. Third-party services and components are subject to their own terms and licenses.

The launcher is provided "AS IS", without warranties of any kind, to the extent permitted by applicable law.

For more information, see the [Minecraft Usage Guidelines](https://www.minecraft.net/en-us/usage-guidelines).

## Русский

Aurora Launcher — независимый неофициальный сторонний лаунчер для Minecraft: Java Edition. Проект не связан с Mojang Studios или Microsoft, не одобрен и не спонсируется этими компаниями.

**ЭТО НЕ ОФИЦИАЛЬНЫЙ ПРОДУКТ MINECRAFT. ПРОДУКТ НЕ ОДОБРЕН И НЕ СВЯЗАН С MOJANG ИЛИ MICROSOFT.**

Minecraft является товарным знаком Mojang Studios. Все права на Minecraft, его название, логотипы, игровые материалы и связанную интеллектуальную собственность принадлежат соответствующим правообладателям.

Aurora Launcher является отдельным программным проектом и не включает в себя и не распространяет копии игры Minecraft. Игровые файлы загружаются из официальных источников, где это возможно. При использовании игры пользователи обязаны соблюдать применимые условия лицензионного соглашения Minecraft и другие соответствующие правила.

Проект не спонсируется и не связан с Modrinth, CurseForge или Microsoft. Названия и товарные знаки сторонних компаний принадлежат их владельцам. На сторонние сервисы и компоненты распространяются собственные условия использования и лицензии.

Лаунчер предоставляется «КАК ЕСТЬ», без каких-либо гарантий в пределах, допускаемых применимым законодательством.

Подробнее: [Правила использования Minecraft](https://www.minecraft.net/en-us/usage-guidelines).
