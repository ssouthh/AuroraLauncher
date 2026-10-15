# Aurora Launcher

> [!IMPORTANT]
> **NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.**
>
> **«ЭТО НЕ ОФИЦИАЛЬНЫЙ ПРОДУКТ MINECRAFT. ПРОЕКТ НЕ ОДОБРЕН MOJANG И MICROSOFT И НЕ СВЯЗАН С НИМИ».**

---

Aurora Launcher — независимый сторонний лаунчер Minecraft: Java Edition для Windows с современным интерфейсом на WinUI 3 (Windows App SDK) и платформе .NET 8.

Проект разработан исключительно для владельцев подлинных лицензионных учетных записей Minecraft Java Edition и строго соблюдает правила Mojang Studios Brand and Assets Guidelines и Microsoft Services Agreement.

---

## Содержание

1. [Правовой статус и дисклеймеры](#правовой-статус-и-дисклеймеры)
2. [Системные требования](#системные-требования)
3. [Регистрация Microsoft Entra ID (OAuth Client ID)](#регистрация-microsoft-entra-id-oauth-client-id)
4. [Сборка и локальный запуск](#сборка-и-локальный-запуск)
5. [Безопасность и защита данных](#безопасность-и-защита-данных)
6. [Загрузчики модов и каталоги](#загрузчики-модов-и-каталоги)
7. [Лицензии компонентов и открытого ПО](#лицензии-компонентов-и-открытого-по)
8. [Сообщение об уязвимостях (Security Disclosure)](#сообщение-об-уязвимостях-security-disclosure)

---

## Правовой статус и дисклеймеры

- **Minecraft**, а также соответствующие логотипы, названия мобов, скинов и игровых ассетов являются товарными знаками или зарегистрированными товарными знаками **Mojang Synergies AB** и **Microsoft Corporation**.
- Aurora Launcher не является официальным продуктом Mojang или Microsoft. Лаунчер не распространяет проприетарные клиентские JAR-файлы, звуки, текстуры или бинарные библиотеки игры в составе своего репозитория или установщика — все компоненты загружаются напрямую с официальных серверов Mojang (`piston-meta.mojang.com`, `libraries.minecraftnet`) с обязательной сверкой контрольных сумм SHA-1.
- В лаунчере **полностью отсутствует и не поддерживается cracked/offline-режим**, предназначенный для обхода авторизации или проверки подлинности лицензии. Для запуска игры требуется действующий профиль Minecraft Java Edition.

---

## Системные требования

- **Операционная система**: Windows 10 (сборка 1809 / 17763 или выше) или Windows 11 (x64 / ARM64).
- **Среда разработки / сборки**: .NET 8 SDK (8.0.x).
- **Среда выполнения**: Windows App SDK Runtime 1.8+ (при сборке с self-contained не требуется).
- **Java**: Java 8, 17, 21 или новее (лаунчер умеет автоматически находить установленные версии или загружать их при необходимости).
- **Лицензия**: Аккаунт Microsoft с приобретенной лицензией Minecraft Java Edition.

---

## Регистрация Microsoft Entra ID (OAuth Client ID)

В соответствии с правилами Microsoft Identity Platform и безопасностью OAuth, приложения не должны использовать чужие Client ID сторонних проектов. Каждый владелец проекта или пользователь может бесплатно зарегистрировать собственный идентификатор приложения в Azure Portal за 2 минуты:

### Пошаговая инструкция

1. Перейдите на портал [Microsoft Entra ID / Azure Portal](https://portal.azure.com/) и войдите под своей личной учетной записью Microsoft.
2. В строке поиска найдите и откройте сервис **Microsoft Entra ID** (или **Azure Active Directory**), затем выберите в левом меню **App registrations** (Регистрация приложений).
3. Нажмите кнопку **+ New registration** (+ Новая регистрация):
   - **Name**: укажите любое понятное имя, например `Aurora Launcher`.
   - **Supported account types**: обязательно выберите третий пункт:
     > *«Accounts in any organizational directory (Any Microsoft Entra ID tenant - Multitenant) and personal Microsoft accounts (e.g. Skype, Xbox)»*
   - **Redirect URI**: выберите тип **Public client/native (mobile & desktop)** и укажите:
     `https://login.live.com/oauth20_desktop.srf`
   - Нажмите **Register** (Зарегистрировать).
4. Настройка аутентификации:
   - В меню созданного приложения перейдите в подраздел **Authentication** (Аутентификация).
   - В разделе *Redirect URIs* при необходимости добавьте также `https://login.microsoftonline.com/common/oauth2/nativeclient`.
   - В блоке **Advanced settings** (Расширенные параметры) найдите пункт **Allow public client flows** (Разрешить потоки общедоступных клиентов) и переключите в положение **Yes** (Да).
   - Нажмите кнопку **Save** (Сохранить) в верхней части страницы.
5. Настройка разрешений API:
   - Перейдите в раздел **API permissions** (Разрешения API) -> **Add a permission** -> вкладка **APIs my organization uses** (или *Microsoft APIs*).
   - Найдите **XboxLive** -> выберите **Delegated permissions** -> отметьте `XboxLive.signin`.
   - Убедитесь, что в списке также присутствует `offline_access` (для обновления токена без повторного логина).
6. Получение Client ID:
   - Перейдите в раздел **Overview** (Обзор) вашего приложения.
   - Скопируйте значение из поля **Application (client) ID** (строка формата GUID `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`).
7. Указание Client ID в Aurora Launcher:
   - **Способ 1 (через настройки)**: Запустите Aurora Launcher -> откройте **Параметры и Настройки** -> вкладка **🔑 Аккаунты и API** -> вставьте скопированный ID в поле **Microsoft Azure OAuth Client ID** -> нажмите *Сохранить*.
   - **Способ 2 (через переменную окружения)**: Установите системную или пользовательскую переменную окружения `AURORA_MS_CLIENT_ID`:
     ```powershell
     [Environment]::SetEnvironmentVariable("AURORA_MS_CLIENT_ID", "ваш-client-id-здесь", "User")
     ```

---

## Сборка и локальный запуск

Исходный код разделен на две части:
- `AuroraLauncher.Core` — независимая библиотека служб (загрузка метаданных Mojang, проверка SHA-1, управление сборками, запуск JVM, шифрование DPAPI).
- `AuroraLauncher.WinUI` — пользовательский интерфейс Windows App SDK / WinUI 3.

### Сборка Debug / Release

```powershell
# Перейдите в каталог AuroraLauncher
cd AuroraLauncher

# Восстановление зависимостей и компиляция
dotnet build .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release
```

### Запуск

```powershell
dotnet run --project .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release
```

### Публикация автономного бинарного дистрибутива (Self-Contained)

```powershell
dotnet publish .\AuroraLauncher.WinUI\AuroraLauncher.WinUI.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false
```

Результат публикации будет расположен в:
`AuroraLauncher.WinUI\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\`

---

## Безопасность и защита данных

- **Хранение секретов**: Все чувствительные данные (токены доступа, сессии Minecraft, персональные ключи API CurseForge) хранятся в защищенном виде с использованием **Windows Data Protection API (DPAPI)** через `System.Security.Cryptography.ProtectedData`. Доступ к расшифровке имеет исключительно локальный профиль пользователя Windows.
- **Пароли пользователей**: Лаунчер никогда не запрашивает, не обрабатывает и не сохраняет пароли пользователей. Вся аутентификация происходит на официальных защищенных веб-узлах Microsoft.
- **Маскирование в логах**: Токены аутентификации (`access_token`, `refresh_token`, сессионные параметры `--accessToken`) строго фильтруются и автоматически маскируются (`***REDACTED***`) перед выводом в консоль отладки и файлы журналов.
- **Телеметрия**: Лаунчер не содержит скрытых модулей сбора телеметрии, слежки, аналитики или передачи персональных данных третьим лицам.
- **Целостность файлов**: Все загружаемые метаданные, клиентские библиотеки, нативные сборки и ресурсы сверяются с официальными хэшами SHA-1 и контрольными размерами байтов перед сохранением на диск.

---

## Загрузчики модов и каталоги

- **Fabric**: Загружается напрямую через официальный API `meta.fabricmc.net` и maven-репозиторий Fabric.
- **Forge / NeoForge**: Официальные JAR-установщики скачиваются с `files.minecraftforge.net` и `maven.neoforged.net` и запускаются через изолированную локальную Java.
- **Modrinth API**: Официальный API открытого каталога модов `api.modrinth.com`.
- **CurseForge API**: Для доступа к каталогу CurseForge требуется личный бесплатный API-ключ, получаемый пользователем на `console.curseforge.com`. Ключ сохраняется в DPAPI-зашифрованном виде.

---

## Лицензии компонентов и открытого ПО

Aurora Launcher опирается на компоненты с открытым исходным кодом:
- **Microsoft Windows App SDK / WinUI 3** — [Microsoft Software License / MIT](https://github.com/microsoft/WindowsAppSDK)
- **System.Security.Cryptography.ProtectedData** — [MIT License](https://github.com/dotnet/runtime)
- **Fabric Meta & Loader** — [Apache License 2.0](https://github.com/FabricMC/fabric-loader)
- **Minecraft Forge / NeoForge** — [LGPL 2.1 / GPL / Open Source](https://github.com/MinecraftForge/MinecraftForge)

Рекомендуемая лицензия для самого репозитория Aurora Launcher: **MIT License** или **GPL-3.0** (на усмотрение владельца проекта).

---

## Сообщение об уязвимостях (Security Disclosure)

Если вы обнаружили потенциальную проблему безопасности или уязвимость в Aurora Launcher:
1. Пожалуйста, не публикуйте информацию об уязвимости в открытых публичных issues.
2. Сообщите авторам проекта через приватный репозиторий GitHub Security Advisories или направьте конфиденциальное сообщение владельцу репозитория.
3. Опишите шаги для воспроизведения и предполагаемое влияние уязвимости. Мы стремимся оперативно устранять любые риски безопасности.
