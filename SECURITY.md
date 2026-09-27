# Security policy

[Русская версия ниже](#политика-безопасности)

UEBulkExport opens files that come from outside: game containers, mappings files, dumps and
paths typed by the user. It also runs helper programs and, for a migration, the Unreal Editor.
Bugs in that handling can matter, so reports are welcome and taken seriously.

## Supported versions

Security fixes go into the latest release only. Please reproduce a problem with the newest
`UEBulkExport-<version>-win-x64-setup.exe` from
[Releases](https://github.com/tavridabless/UEBulkExport/releases) before reporting it.

| Version | Supported |
|---|---|
| Latest 2.x release | Yes |
| Older releases | No — please update |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a vulnerability.** Report it
privately through GitHub instead:

1. Open the repository's **Security** tab and choose **Report a vulnerability**, or go straight
   to <https://github.com/tavridabless/UEBulkExport/security/advisories/new>.
2. Describe what you found:
   - the UEBulkExport version, Windows version, and whether it is the window or the command line;
   - the steps to reproduce, and what happens compared with what you expected;
   - the impact as you see it (for example: code execution, writing outside the output folder,
     leaking an AES key);
   - a proof of concept, if you have one. Please send a minimal crafted file rather than files
     from a commercial game: we cannot accept copyrighted game data.
3. Keep the details private until a fix is released.

Only you and the maintainers can see the report. If you cannot use GitHub, open a public issue
that says only that you have a security report and need a private channel — without any details.

## What happens next

| Step | Target |
|---|---|
| Acknowledgement of your report | within 3 working days |
| First assessment and severity | within 10 working days |
| Fix for a confirmed high or critical issue | within 30 days where possible |

We keep you informed along the way, agree on a disclosure date with you, and publish a GitHub
security advisory together with the fixed release. With your consent you are credited in the
advisory and in the changelog; you can also stay anonymous. A CVE is requested through GitHub
when the issue warrants one.

## Scope

In scope — for example:

- code execution or memory corruption triggered by a crafted `.pak`, `.utoc`, `.ucas`, `.usmap`
  or dump file;
- writing, overwriting or deleting files outside the chosen output folder or the target project
  (path traversal in entry names, links, the migration working copy);
- injection into the commands or the Python script UEBulkExport builds for UE Viewer, retoc or
  the Unreal Editor;
- loading or running a helper binary or library that an attacker could plant or replace;
- AES keys or other secrets exposed in logs, reports, the equivalent command line or
  `%LOCALAPPDATA%\UEBulkExport`;
- privilege problems in the installer or the uninstaller.

Out of scope:

- extracting assets from a game — that is what the tool is for; whether you may use them is a
  licensing question, see the README's legal section;
- vulnerabilities in CUE4Parse, retoc, UE Viewer, UE4SS or Unreal Engine themselves — please
  report those to their authors, and tell us if the way UEBulkExport uses them makes things worse;
- the SmartScreen warning for the unsigned installer;
- problems that require an attacker who already has administrator rights on the machine;
- denial of service by feeding the tool a huge or endless file on your own computer.

## How UEBulkExport handles code from the network

So you know what to look at:

- **retoc** is downloaded from its GitHub release over HTTPS and checked against a pinned SHA-256
  hash, both for the archive and for the executable, before it runs.
- **Native libraries** (zlib-ng, Oodle, Detex and CUE4Parse's native helper) are downloaded over
  HTTPS on first use when they are not already present, and cached in
  `%LOCALAPPDATA%\UEBulkExport\native`. These downloads are not yet pinned to a hash; hardening
  them is planned.
- Nothing else is sent over the network. UEBulkExport has no telemetry and never uploads files.

## Safe harbour

Good-faith research is welcome. Test on your own computer and with your own data, do not access
data that is not yours, do not degrade anyone else's use of the project, and give us a reasonable
chance to fix the issue before disclosure. We will not pursue or support action against research
done in that spirit.

---

# Политика безопасности

UEBulkExport открывает файлы, пришедшие извне: игровые контейнеры, файлы маппингов, дампы и пути,
которые вводит пользователь. Кроме того, программа запускает вспомогательные утилиты, а при
переносе — Unreal Editor. Ошибки в этой обработке могут быть опасны, поэтому мы благодарны за
отчёты и относимся к ним серьёзно.

## Поддерживаемые версии

Исправления безопасности выходят только в последнем релизе. Прежде чем сообщать о проблеме,
проверьте её на свежем `UEBulkExport-<версия>-win-x64-setup.exe` со страницы
[Releases](https://github.com/tavridabless/UEBulkExport/releases).

| Версия | Поддерживается |
|---|---|
| Последний релиз 2.x | Да |
| Более старые релизы | Нет — обновитесь |

## Как сообщить об уязвимости

**Не создавайте публичный issue, обсуждение или pull request об уязвимости.** Отправьте приватный
отчёт через GitHub:

1. Откройте вкладку **Security** репозитория и выберите **Report a vulnerability** или перейдите
   сразу на <https://github.com/tavridabless/UEBulkExport/security/advisories/new>.
2. Опишите находку:
   - версию UEBulkExport, версию Windows, окно или командная строка;
   - шаги воспроизведения, что происходит и что вы ожидали;
   - последствия, как вы их видите (например, выполнение кода, запись за пределы папки вывода,
     утечка AES-ключа);
   - доказательство, если оно есть. Присылайте минимальный специально подготовленный файл, а не
     файлы коммерческих игр: принимать чужие защищённые авторским правом данные мы не можем.
3. Не раскрывайте подробности, пока не выйдет исправление.

Отчёт видите только вы и мейнтейнеры. Если воспользоваться GitHub нельзя, создайте публичный issue,
в котором напишите только, что у вас есть отчёт об уязвимости и нужен приватный канал, — без
подробностей.

## Что будет дальше

| Этап | Срок |
|---|---|
| Подтверждение получения | в течение 3 рабочих дней |
| Первичная оценка и степень опасности | в течение 10 рабочих дней |
| Исправление подтверждённой высокой или критической проблемы | по возможности в течение 30 дней |

Мы держим вас в курсе, согласуем с вами дату раскрытия и публикуем security advisory на GitHub
вместе с исправленным релизом. С вашего согласия вы будете указаны в advisory и в журнале изменений;
можно остаться анонимным. Если проблема этого заслуживает, CVE запрашивается через GitHub.

## Что относится к безопасности

Относится, например:

- выполнение кода или повреждение памяти из-за специально подготовленного `.pak`, `.utoc`, `.ucas`,
  `.usmap` или файла дампа;
- запись, перезапись или удаление файлов за пределами выбранной папки вывода или целевого проекта
  (обход пути в именах записей, ссылки, рабочая копия при переносе);
- внедрение в команды или Python-скрипт, которые UEBulkExport формирует для UE Viewer, retoc или
  Unreal Editor;
- загрузка или запуск вспомогательной программы или библиотеки, которую злоумышленник может
  подложить или подменить;
- AES-ключи или другие секреты в журналах, отчётах, эквивалентной команде или в
  `%LOCALAPPDATA%\UEBulkExport`;
- проблемы с правами у установщика или программы удаления.

Не относится:

- извлечение ассетов из игры — ради этого инструмент и существует; можно ли их использовать —
  вопрос лицензии, см. правовой раздел README;
- уязвимости самих CUE4Parse, retoc, UE Viewer, UE4SS или Unreal Engine — сообщайте о них их авторам
  и расскажите нам, если то, как их использует UEBulkExport, усугубляет проблему;
- предупреждение SmartScreen о неподписанном установщике;
- проблемы, для которых злоумышленнику уже нужны права администратора на компьютере;
- отказ в обслуживании из-за огромного или бесконечного файла на вашем же компьютере.

## Что UEBulkExport получает из сети

Чтобы было понятно, куда смотреть:

- **retoc** скачивается из релиза на GitHub по HTTPS и перед запуском сверяется с закреплённым
  хэшем SHA-256 — и архив, и сам исполняемый файл.
- **Нативные библиотеки** (zlib-ng, Oodle, Detex и нативный модуль CUE4Parse) скачиваются по HTTPS
  при первом использовании, если их ещё нет, и кешируются в `%LOCALAPPDATA%\UEBulkExport\native`.
  Эти загрузки пока не сверяются с закреплённым хэшем; усиление проверки запланировано.
- Больше ничего по сети не передаётся. Телеметрии нет, файлы никуда не отправляются.

## Добросовестное исследование

Мы рады добросовестным исследованиям. Проверяйте на своём компьютере и на своих данных, не
получайте доступ к чужим данным, не мешайте другим пользоваться проектом и дайте нам разумное время
на исправление перед раскрытием. Против исследований, проведённых в таком духе, мы не будем
предпринимать и поддерживать никаких действий.
