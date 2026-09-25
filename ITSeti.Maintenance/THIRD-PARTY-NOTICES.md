# Сторонние компоненты

## LibreHardwareMonitorLib 0.9.6

Используется для чтения температуры процессора. Библиотека включена без
изменений и распространяется по Mozilla Public License 2.0 (MPL-2.0).

- Исходный код: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Текст MPL-2.0: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE
- Уведомления о лицензиях компонентов: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/THIRD-PARTY-NOTICES.txt

## PawnIO 2.2.0

Официальный подписанный установщик PawnIO распространяется без изменений из
https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0 . SHA-256 файла:
`1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`.
Отпечаток подписанта Authenticode:
`F380DCC9F706E2756A5047B832FFE719E1BC35F5`.

PawnIO лицензирован по GPL-2.0-or-later с дополнительным исключением для
независимых LGPL-модулей, взаимодействующих через IOCTL-интерфейс устройства.
Исходный код и лицензия: https://github.com/namazso/PawnIO . Установщик остаётся
отдельным неизменённым компонентом поставщика. Исходный код приложения публикуется
в том же открытом репозитории, что и установочный пакет.

Приложение не отключает и не обходит антивирус или защиту драйверов Windows.
Если проверка подписи/хеша или установка драйвера не проходит, приложение
устанавливается и продолжает работу, но температура CPU будет отмечена как
недоступная.
