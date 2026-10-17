# Автоопределение метаданных EXE на Linux

Исходная ошибка: системный FileVersionInfo на Linux не считывает нужные ресурсы native Inno Setup EXE. При canonical filename без версии резерв по имени тоже отсутствует, поэтому загрузка с пустой версией возвращала 400.

Добавлен переносимый reader PE RT_VERSION: ProductName/FileDescription, ProductVersion/FileVersion и VS_FIXEDFILEINFO. Установщик не запускается. RVA и размеры проверяются, дерево ограничено глубиной/числом узлов, каждый payload не превышает 64 КиБ; файл целиком не загружается. Некорректный/не-PE/обрезанный файл сохраняет штатные FileVersionInfo и filename fallback.

Спецификация: [Microsoft PE resources](https://learn.microsoft.com/en-us/windows/win32/debug/pe-format#the-rsrc-section), [VERSIONINFO](https://learn.microsoft.com/en-us/windows/win32/menurc/versioninfo-resource).

Проверен реальный установщик 2.4.11: 104377663 байта, SHA256 C50003C6314D628BFBDC3E9BC9A120C76A838150BA0E45CC364F7DA99AF3911D. Вызов Detect с именем ITSeti-Maintenance-Setup.exe возвращает ИТ-Сети Обслуживание ПК / 2.4.11. При заголовке unrelated-9.9.9.exe встроенные метаданные сохраняют приоритет. Загрузка приложения штатно нормализует эту версию в 2.4.11.0.

Контракт --pe-metadata-contract <EXE> реально выполнен на Windows и внутри Ubuntu 24.04.5 контейнера с .NET10, network none, бинарник подключён read-only и не выполнялся. Дополнительно пустой файл, неверный MZ, нулевой файл и обрезанный настоящий PE безопасно используют fallback. Release build 0 предупреждений; существующий server suite прошёл.

Для проверки: dotnet ITSeti.Server.Tests.dll --pe-metadata-contract /fixture/setup.exe (fixture — указанный EXE 2.4.11). Рабочий сервер этим коммитом не развёртывался.
