# Windows EXE: backlog и шкала готовности

Документ относится только к Windows EXE/client-направлению:

```text
свой AIKE/IKE-like exchange -> ручная установка ESP SA -> прямой ESP proto 50
```

Сюда не входят Linux/OpenWRT/MikroTik, EVE-топология, текст диссертации и будущие режимы WireGuard/Amnezia.

## Текущее состояние

Текущая оценка готовности Windows EXE MVP/prototype: **38%**.

Это взвешенная оценка по сложности инженерных задач, а не подсчёт количества пунктов. Она учитывает фактическое состояние проекта:

- уже есть `manual-sa.exe` и PoC работы с Windows WFP/manual SA API;
- локальные smoke-тесты WFP/IPsec API проходили;
- Windows11-U13 частично прошла peer-side SA install;
- финальный чистый `host <-> U13` тест с `pktmon` и ESP proto 50 ещё не закрыт;
- cleanup/retry lifecycle сейчас доделывается параллельно;
- service/CLI/core skeleton создаётся параллельно, но пока не интегрирован с AIKE/manual-SA;
- спецификация custom AIKE exchange уже проработана, но end-to-end реализация в Windows-клиенте ещё не готова.

Цель этой шкалы - исследовательский Windows EXE/client prototype. Это ещё не коммерческий продукт с подписанным installer-ом, красивым tray GUI и production-hardening.

## Взвешенная модель

| Область | Вес | Готовность сейчас | Вклад сейчас | Комментарий |
|---|---:|---:|---:|---|
| Windows WFP/manual SA engine | 18% | 70% | 12.6% | `manual-sa.exe` уже есть, WFP/manual SA вызовы локально доказаны, но lifecycle и повторяемость ещё не закрыты. |
| Реальное доказательство Windows endpoint | 12% | 45% | 5.4% | U13 частично поставила SA; host completion, ping и `pktmon` ESP capture ещё надо закрыть. |
| Custom AIKE protocol implementation | 16% | 15% | 2.4% | Протокол и crypto-design есть, но state machine ещё не интегрирована. |
| Crypto/auth implementation | 12% | 20% | 2.4% | X25519/HKDF/AES-GCM/Ed25519 определены, но library integration и transcript checks не закрыты. |
| Metadata privacy и разделение знания маршрута | 8% | 20% | 1.6% | Архитектура понятна, но Windows-реализация ещё не связана с exchange. |
| Cleanup/retry/SA lifecycle | 10% | 25% | 2.5% | Удаление stale SA/filter и безопасный retry - текущий активный инженерный слой. |
| MVP CLI orchestration | 8% | 20% | 1.6% | Есть скриптовая обвязка вокруг `manual-sa`, но полноценного `connect/status/cleanup` CLI ещё нет. |
| Windows service/background mode | 8% | 20% | 1.6% | База service есть/создаётся, но это ещё не AIKE/manual-SA orchestrator. |
| Config и identity storage | 4% | 20% | 0.8% | Форма config понятна; DPAPI lifecycle для identity/private keys ещё не готов. |
| Logs и diagnostics | 2% | 35% | 0.7% | Логи/скрипты есть, но structured diagnostics продукта ещё не готовы. |
| Installer/user packaging | 2% | 10% | 0.2% | Есть packaging-заделы, но это ещё не пользовательский установщик. |

Суммарный вклад по фактически доказанным частям: **около 32%**.

Дополнительный вклад reusable skeleton/specification: **около 6%**.

Практическая округлённая оценка: **38%**.

## Milestone 1: Research PoC

Цель: доказать, что Windows может участвовать без собственного kernel-driver: свой control/exchange path устанавливает ESP SA вручную через WFP/manual SA, а data-plane идёт как ESP proto 50.

| Задача | Вес | Статус |
|---|---:|---|
| Собрать `manual-sa.exe` как WFP/manual SA engine | 8% | Почти готово |
| Локальный WFP API smoke test | 4% | Готово |
| Host-side `host-exchange` lifecycle | 4% | Частично готово |
| Peer-side U13 SA install | 5% | Частично готово |
| Clean host completion по peer SPI | 4% | В работе |
| Direct ESP ping и `pktmon` proto 50 capture | 5% | Не закрыто |
| Cleanup после failed/partial session | 4% | В работе |
| Research PoC документация | 2% | Частично готово |

Вес Research PoC: **36%**.

Текущая готовность Research PoC в общей шкале: **примерно 27-29% из 100%**.

Ближайшая закрываемая цепочка:

```text
cleanup
-> host-exchange
-> U13 peer install
-> host complete
-> ping
-> pktmon proto 50
```

После закрытия этой цепочки ожидаемый прирост: **+6-8%**.

Новая оценка после закрытия: **44-46%**.

## Milestone 2: MVP CLI

Цель: разработчик может запустить Windows CLI-клиент по config и повторяемо поднять/убрать одну защищённую ESP-сессию.

| Задача | Вес | Статус |
|---|---:|---|
| `adaptive-client.exe connect <peer>` | 4% | Не интегрировано |
| `adaptive-client.exe status` | 2% | Не интегрировано |
| `adaptive-client.exe cleanup <peer>` | 2% | Пока script-level |
| AIKE state machine: init, KE1, KE2, metadata, SPI, install ACK | 8% | Только spec |
| Ed25519 signature verification | 3% | Только spec |
| X25519 + HKDF key schedule | 3% | Spec/prototype-level |
| AES-GCM encrypted metadata | 3% | Только spec |
| Replay cache и timeout handling | 3% | Только spec |
| Config v1: node id, peers, keys, policy, lifetimes | 3% | Draft |
| Repeatable cleanup/retry around failed sessions | 4% | В работе |

Вес MVP CLI: **35%**.

Ожидаемая готовность после MVP CLI: **65-68%**.

## Milestone 3: Service Alpha

Цель: клиент работает как Windows background service, а CLI управляет service вместо прямого запуска низкоуровневых команд.

| Задача | Вес | Статус |
|---|---:|---|
| Windows service запускает AIKE/control listener | 4% | Skeleton/parallel work |
| Service безопасно вызывает manual-SA engine | 3% | Не интегрировано |
| Service-to-CLI IPC | 3% | Не интегрировано |
| Startup cleanup stale SA/filter state | 3% | Запланировано |
| Rekey/expire/remove SA lifecycle manager | 3% | Запланировано |
| Structured logs with session ids | 2% | Частично |
| Sleep/wake/network-change handling | 2% | Не начато |

Вес Service Alpha: **20%**.

Ожидаемая готовность после Service Alpha: **82-85%**.

## Milestone 4: User-Facing Beta

Цель: тестировщик не из разработки может установить клиент, импортировать config, подключиться, отключиться, увидеть статус и собрать диагностику.

| Задача | Вес | Статус |
|---|---:|---|
| Tray UI: status/connect/disconnect | 3% | Не начато |
| Import/export config workflow | 2% | Не начато |
| Diagnostics bundle без private secrets | 2% | Не начато |
| Friendly error mapping | 2% | Не начато |
| Basic installer/uninstaller | 3% | Есть только packaging-заделы |

Вес User-Facing Beta: **12%**.

Ожидаемая готовность после Beta: **92-94%**.

## Milestone 5: Production

Цель: hardenable Windows client, пригодный для более широкого распространения.

| Задача | Вес | Статус |
|---|---:|---|
| DPAPI-backed identity/private key storage | 2% | Спроектировано |
| ACL hardening для config, keys, logs | 1.5% | Запланировано |
| Secure zeroization и review lifetime secret-ов | 1.5% | Запланировано |
| Upgrade/repair/rollback installer behavior | 1.5% | Запланировано |
| Code signing/release process | 1% | Запланировано |
| Compatibility pass: Windows 10/11, reboot, sleep, network switch | 2.5% | Не начато |

Вес Production: **10%**.

Ожидаемая готовность после Production hardening: **100%** по Windows EXE-track.

## Следующие приросты

| Следующий milestone | Прирост | Новая оценка | Почему важно |
|---|---:|---:|---|
| Доделать cleanup/retry для `manual-sa` | +3-4% | 41-42% | Тесты станут повторяемыми, stale SA/filter перестанут ломать следующие прогоны. |
| Закрыть чистый host `<->` U13 ESP capture | +5-6% | 44-46% | Windows endpoint станет доказанным фактом: прямой ESP proto 50, а не частичная установка SA. |
| Реализовать минимальную AIKE state machine в CLI | +8-10% | 52-56% | Переход от скриптов/manual exchange к поведению настоящего EXE-клиента. |
| Подключить реальный X25519/HKDF/AES-GCM/Ed25519 flow | +7-8% | 60-64% | Exchange станет криптографически полноценным, а не только orchestration layer. |
| Добавить config v1 и peer identity storage | +4-5% | 65-69% | Появятся повторяемые подключения по конфигу вместо hardcoded tests. |
| Обернуть в Windows service с CLI IPC | +10-12% | 76-81% | Прототип станет background-клиентом. |
| Добавить lifecycle manager: rekey, expire, startup cleanup | +5-6% | 82-86% | Сервис сможет жить долго, а не только проходить один тест. |
| Добавить installer/tray/diagnostics | +8-10% | 90-95% | Клиент станет пригодным для тестировщиков без ручного запуска скриптов. |

## Definition of Done

### Текущий слой

Текущий слой считается закрытым только когда с чистого состояния проходит цепочка:

```text
1. cleanup host-side stale state
2. cleanup peer-side stale state
3. start host exchange
4. peer receives host SPI
5. peer installs inbound and outbound ESP SA
6. host receives peer SPI
7. host installs outbound ESP SA
8. ping succeeds
9. pktmon confirms ESP / IP protocol 50 packets
10. cleanup removes the session cleanly
```

Пока пункт 9 не зафиксирован, Windows endpoint support нужно описывать как **частично доказанный**, а не полностью закрытый.

### MVP CLI

MVP CLI считается готовым, когда работает:

```powershell
adaptive-client.exe connect User13
adaptive-client.exe status
adaptive-client.exe cleanup User13
```

и клиент выполняет:

- AIKE exchange;
- authenticated metadata exchange;
- SPI negotiation;
- ESP key derivation;
- WFP/manual SA install;
- direct ESP data-plane;
- cleanup/retry после ошибки.

## Риски

- Самый большой текущий риск - не протокол, а стабильный Windows manual SA lifecycle и повторяемый cleanup.
- Второй риск - аккуратная передача AIKE-derived ESP material в Windows WFP structures без хрупкой script-only обвязки.
- Kernel-driver остаётся будущим high-control вариантом, но этот backlog ведёт no-custom-driver Windows EXE path.
- Эту процентную шкалу нельзя напрямую сравнивать с Linux/OpenWRT: Windows EXE - отдельный product track с большим количеством platform lifecycle-работы.
