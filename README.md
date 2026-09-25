<div align="center">
  <img src="ITSeti.Maintenance/src/ITSeti.Maintenance.App/Assets/logo.png" alt="ИТ-Сети" width="260">
  <h1>Обслуживание ПК ИТ-Сети</h1>
  <p>Диагностика и обслуживание Windows для пользователей и инженеров</p>
</div>

![Главное окно пользователя](ITSeti.Maintenance/docs/images/user-home.png)

**Текущая версия: 0.9.5** · [Скачать установщик](https://github.com/izivinizi/IT-Seti_client/releases/latest/download/ITSeti-Maintenance-Setup.exe) · [Все релизы](https://github.com/izivinizi/IT-Seti_client/releases)

## Возможности

| Пользователь | Инженер |
| --- | --- |
| Понятная сводка CPU, ОЗУ и системного диска | Полная и ускоренная диагностика компьютера |
| SMART и скорость накопителя без окон тестовых программ | Состояние дисков, SMART, процессы и фильтруемые события Windows |
| Причины возможных проблем простым языком | История проверок, сравнение результатов и отладочные журналы |
| Очистка корзины и временных файлов | Очистка, DISM/SFC, обслуживание и инструменты ИТ-Сети |

Диагностика оценивает свободное место, температуру CPU, загрузку ресурсов,
состояние накопителей, скорость чтения, сетевые адаптеры и время после
перезагрузки. Предупреждения помогают заметить отклонения, но не заменяют
проверку оборудования инженером.

## Обновление

Проверка обновлений не использует GitHub API: приложение загружает `release.json`
из последнего стабильного релиза, поэтому не упирается в лимит анонимных API-запросов.
Установка выполняется системной задачей и перед запуском проверяет версию,
размер, доверенный адрес GitHub и SHA-256 установщика. Пароли и токены в проект
не включены.

## Установка и сборка

Скачайте `ITSeti-Maintenance-Setup.exe` со страницы
[последнего релиза](https://github.com/izivinizi/IT-Seti_client/releases/latest).
Установка требует прав администратора для регистрации системных задач.
Приложение рассчитано на Windows 10/11 x64; для Windows 7 оставлена отдельная
скриптовая версия.

Исходный код находится в [`ITSeti.Maintenance`](ITSeti.Maintenance/README.md).
Для локальной сборки нужны .NET 9 SDK и Inno Setup 6:

```powershell
dotnet build ITSeti.Maintenance/ITSeti.Maintenance.sln
dotnet run --project ITSeti.Maintenance/tests/ITSeti.Maintenance.Smoke
pwsh -File ITSeti.Maintenance/Build-InstallPackage.ps1
```

В `ITSeti.Maintenance/Tools` включены CrystalDiskInfo, CrystalDiskMark и
TreeSize Free. Условия распространения сторонних компонентов описаны в
[`THIRD-PARTY-NOTICES.md`](ITSeti.Maintenance/THIRD-PARTY-NOTICES.md).
