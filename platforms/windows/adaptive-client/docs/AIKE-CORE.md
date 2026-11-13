# AIKE Core (Skeleton)

Цель: `Windows EXE -> AIKE-like exchange -> manual-sa -> ESP proto 50`.

## Wire format (MVP → далее)

- На этом этапе контрол-плэйн использует `AikeMessageCodec` с **UTF-8 JSON**.
- Формат сейчас считается debug/MVP и не оптимизирован для production.
- В следующей итерации планируется переход на **canonical CBOR** с детерминированной кодировкой и строгой схемой.

## Что реализовано сейчас

В `AdaptiveClient.Core` сейчас есть минимальная не-продакшн машина состояний для smoke-подтверждения:

```text
Created -> Ke1Sent -> Ke2Sent -> Established
Created -> Ke1Received -> Ke2Sent -> Established
```

И отдельный финишный статус:

`Failed`.

## Состояния

- `Created` — начальное состояние сессии.
- `Ke1Sent` — инициатор отправил KE1.
- `Ke1Received` — респондер получил KE1 и перешёл к обработке, готовность отправить KE2.
- `Ke2Sent` — сторона отправила KE2 и завершила обмен ключами.
- `Established` — обмен KE1/KE2 подтверждён, готовы рассчитывать `EspMaterial` и запускать manual-sa (по отдельному слою приложения).
- `Failed` — фатальная ошибка, сессия перестаёт обрабатываться.

## API и смысл

### `AikeMessageCodec`

- `Encode(AikeMessage)` — сериализует envelope в UTF-8 JSON-строку.
- `EncodeToBytes(AikeMessage)` — сериализует в UTF-8 JSON байты.
- `Decode(string)` — распаковывает message из JSON строки.
- `Decode(byte[])` — распаковывает message из UTF-8 JSON байтов.

Кодек использует стабильные `JsonSerializerOptions` (`snake_case`, `Utf8`, `StringEnumConverter`, без лишней информации в логах), чтобы smoke-пайплайн был воспроизводимым.

Также в Core добавлен небольшой console-free demo helper:

- `AikeMessageCodecSmoke.RunKe1Ke2RoundTrip()`
- `AikeMessageCodecSmoke.RunKe1Ke2EstablishedRoundTrip()`
- Поднимает лабовый диалог:
  - инициатор через `CreateSession` формирует `KE1`,
  - кодируется/декодируется через `AikeMessageCodec`,
  - респондер принимает `KE1`, формирует и отправляет `KE2`,
  - инициатор получает и обрабатывает `KE2`.
- `RunKe1Ke2EstablishedRoundTrip()` идёт дальше до `Established` через `SpiOffer/InstallAck` путь
  и возвращает структурный результат:
  - `Success`
  - `FinalState`
  - `SessionId`
  - `Error`.
- `RunEstablishedAndDeriveEspMaterial()` добивает путь до manual-sa boundary:
  - проходит `CreateSession -> KE1 -> KE2 -> Established`;
  - вызывает `DeriveEspMaterial(...)` с dummy values:
    - local IP `192.168.95.1`
    - remote IP `192.168.113.199`
    - dummy inbound/outbound SPIs
  - возвращает: `Success`, `FinalState`, `SessionId`, `Protocol`, `InboundSpi`, `OutboundSpi`, `Error`.
- `RunEstablishedAndBuildManualSaInstallPlan()` дополнительно валидирует профили:
  - профиль ESP ключей из `EspProfiles.ManualSa` требует:
    - inbound/outbound cipher key length `16` байт,
    - inbound/outbound auth key length `32` байта.
- `RunEstablishedAndBuildManualSaInstallPlan()` возвращает boundary contract `ManualSaInstallPlan`:
  - `local_ip`, `remote_ip`, `ipsec_protocol`, `traffic_selector_protocol`, `inbound_spi`, `outbound_spi`;
  - `ipsec_protocol` фиксирован на `ESP`/`50`;
  - `traffic_selector_protocol` в MVP используется как L4-протокол для manual-sa CLI (`icmp` в smoke);
  - key placeholders as byte arrays;
  - `profile` с именем и ожидаемыми lengths (`cipher`, `auth`);
  - log-safe summary без раскрытия ключей;
  - возвращает `success/final_state/session_id/error/validation`:
    - невалидность проверяется `ManualSaInstallPlanValidator.Validate(plan)`,
    - правила: непустые и разные `local_ip`/`remote_ip`;
      - `ipsec_protocol` должен быть `ESP` или `50`;
      - `traffic_selector_protocol` должен быть `icmp`, `any` или числовым L4-протоколом;
      - inbound/outbound SPI nonzero и разные;
      - все 4 key-placeholder массива длинны по профилю.
- `ManualSaInstallPlan` формируется через mapper `EspMaterial.ToManualSaInstallPlan()`.
- `ManualSaPlanFileSerializer` добавлен для serialization boundary в manual-sa plan-file v1:
  - `ToKeyValuePlan(plan)` -> text with ключами:
    - `local`, `remote`, `selector_protocol`, `in_spi`, `out_spi`, `in_cipher_key`, `in_auth_key`, `out_cipher_key`, `out_auth_key`.
  - в full версии ключи передаются в hex (байты сериализуются как hex-строки),
  - `ToRedactedKeyValuePlan(plan)` -> той же топологии без secret material, только длины `*_len`.
- `RunPlanFileBoundaryRoundTrip()`:
  - строит план через `RunEstablishedAndBuildManualSaInstallPlan()`,
  - сериализует его в plan-file v1,
  - строит redacted summary,
  - валидирует roundtrip без записи файлов.
- `RunDynamicEndpointPlanFileBoundaryRoundTrip()`:
  - создаёт lab `EndpointRecord` для local/remote node identities,
  - резолвит latest valid endpoint по `node_id` через `EndpointDirectorySnapshot`,
  - строит `ManualSaInstallPlan` из current endpoint records,
  - сериализует plan-file v1 и валидирует roundtrip без записи файлов.
- Для этого используется только `DeterministicLabAikeCrypto` (NOT FOR PRODUCTION), без внешних пакетов.

### `CreateSession(...)`

- Проверяет, что локальный и удалённый peer имеют id/key и доверены trust-store.
- Генерирует `sessionId` через `IAikeCrypto`.
- Генерирует локальный ephemeral для KE1.
- Строит и подписывает `KE1` (через `IAikeCrypto`).
- Переходит в `Ke1Sent` и возвращает `AikeSessionStart(SessionState, AikeMessage)`.

### `ProcessMessage(...)`

Возвращает `ProcessResult`:

- `Success=false` с сообщением ошибки — для всех неконсистентных кейсов (не тот state, подпись/транскрипт/последовательность).
- `Success=true` — сессия обновлена и, при необходимости, есть `OutboundMessage` для отправки обратно.

Поддерживаемые переходы:

1. `Ke1`:
   - только для роли `Responder` в состоянии `Created`;
   - сохраняет удалённый ephemeral;
   - генерирует `Ke2` и возвращает его как `OutboundMessage`;
   - переводит в `Ke2Sent` (после локальной отправки KE2).

2. `Ke2`:
   - только для роли `Initiator` в состоянии `Ke1Sent`;
   - сохраняет удалённый ephemeral;
   - переводит в `Ke2Sent`.

3. `SpiOffer`/`InstallAck`:
   - при корректном состоянии переводят в `Established`;
   - используются как явный скелетный финиш handshake.

4. `ProtectedMetadata`/`AddrToResponder` — проксируются с валидацией и базовыми проверками хешей.

### `DeriveEspMaterial(...)`

- Проверяет `Phase == Established`, что key exchange выполнен и сессия не просрочена.
- Запрашивает `IAikeCrypto.DeriveEspKeys(session)`.
- Возвращает `EspMaterial` с `Protocol="50"` (IPsec protocol), `Profile` (`ESP_AES_128_CBC_HMAC_SHA256_128` в smoke-канфиге) и mode `Transport`.
- Для логирования без секретов используйте `EspMaterial.ToLogSafeSummary()` / `EspMaterial.ToLogSafe()` и не печатайте `Inbound/Outbound*Key` напрямую.

### `ESP profile compatibility with manual-sa`

- В DTO разделены два протокола:
  - `ipsec_protocol` (или `Protocol` в `EspMaterial`) — всегда IPsec, для текущего MVP это `ESP`/`50`.
  - `traffic_selector_protocol` — L4 поле для `manual-sa --protocol` (`icmp`/число/`any`), не ESP.
- `EspProfile`/`EspKeyLengths` задают ожидаемые длины ключей по профилю.
- Для текущего manual-sa профиля:
  - `profile = ESP_AES_128_CBC_HMAC_SHA256_128`;
  - `cipher_key_len = 16` bytes;
  - `auth_key_len = 32` bytes.
- `ManualSaInstallPlan` переносит профиль (`Profile`) и инлайнит lengths, чтобы валидация могла быть profile-specific.
- `ManualSaInstallPlan.Validator` теперь проверяет длины плейсхолдеров по profile-specific lengths.
- Маппинг direction:
  - `Inbound*` из `EspMaterial` → inbound install SA направления;
  - `Outbound*` из `EspMaterial` → outbound install SA направления.

### `Dynamic endpoint records`

Core теперь разделяет identity и текущий IP:

- Identity:
  - `node_id`/`peer_id`;
  - identity key (`PeerDescriptor.KeyId` / `PublicIdentityKey`).
- Endpoint:
  - `EndpointRecord.NodeId`;
  - `Ip`;
  - optional `Port`;
  - `SelectorProtocol`;
  - `Source`;
  - `Sequence`;
  - `ValidFrom`;
  - `ExpiresAt`;
  - `Signature` placeholder.

`EndpointRecord` не считается секретом, но не должен приниматься как доверенный без проверки:

- `EndpointRecordValidator.Validate(record, now, previousSeq?)` проверяет:
  - `node_id`, `ip`, `selector_protocol`, `source`;
  - optional `port` в диапазоне `1..65535`;
  - `sequence != 0`;
  - если задан `previousSeq`, новый `sequence` должен быть больше;
  - `valid_from <= now < expires_at`;
  - наличие signature placeholder.
- На текущем skeleton-этапе signature только placeholder, production crypto не реализована.

`EndpointDirectorySnapshot` — immutable resolver для dry-run:

- хранит набор `EndpointRecord`;
- `ResolveLatest(nodeId, now)` выбирает latest valid endpoint по максимальному `Sequence`;
- expired/future/invalid records игнорируются;
- возвращает `EndpointDirectoryResolveResult` с log-safe endpoint summary.

MOBIKE-lite смысл:

- AIKE identity остаётся стабильной: node id + identity key.
- IP может меняться отдельно от identity.
- При смене IP сторона публикует новый `EndpointRecord` с большим `sequence`.
- Service резолвит current local/remote endpoints и строит новый `ManualSaInstallPlan`.
- После endpoint update старые SA должны быть переустановлены или rekey/reinstall выполнен внешним orchestration layer.

`EspMaterial.ToManualSaInstallPlan(localEndpoint, remoteEndpoint)` строит plan из current endpoints:

- `plan.LocalIp` берётся из local endpoint;
- `plan.RemoteIp` берётся из remote endpoint;
- `plan.TrafficSelectorProtocol` берётся из endpoint selector protocol;
- local/remote selector protocols должны совпадать или одна сторона должна быть `any`;
- key material и SPI остаются из `EspMaterial`.

### `VerifyTranscript(...)`

- Проверяет статус/срок действия/наличие key exchange.
- Делегирует финальную проверку transcript в `IAikeCrypto`.

## Crypto stub для лаба

Добавлен `DeterministicLabAikeCrypto : IAikeCrypto`:

- детерминированный (predictable) для unitless smoke,
- без криптографической стойкости,
- помечен `[Obsolete("...NOT FOR PRODUCTION")]`,
- подходит только для ранней интеграции/локального прогонки, **не для релиза**.

## Build command

```powershell
dotnet build .\windows-agent\adaptive-client\AdaptiveClient.Core\AdaptiveClient.Core.csproj --no-restore
```

## Как должен вызывать Core из Service

На стороне `AdaptiveClient.Service` (вне зоны редактирования) вызов выглядит так:

1. Создать `AikeSessionManager` с реализацией `IAikeCrypto` и `IPeerTrustStore`.
2. Для подключения:
   - `CreateSession(...)` для инициатора.
   - отправить `FirstMessage` (KE1).
3. При каждом входящем AIKE-сообщении:
   - сначала распаковать payload из сети:
     - `AikeMessageCodec.Decode(bytes)` для binary transport;
     - или `AikeMessageCodec.Decode(string)` если сеть передаёт UTF-8 строку.
   - затем вызвать `ProcessMessage(...)`.
   - если `ProcessResult.Success=false` — логировать ошибку и перейти в `cleanup`.
   - если есть `OutboundMessage` — сериализовать через `AikeMessageCodec.Encode(...)` или `EncodeToBytes(...)` и отправить.
4. Когда фаза `Established`, вызвать `DeriveEspMaterial(...)`.
5. После обмена подтверждающими сообщениями (по вашему пайплайну) вызвать `VerifyTranscript(...)` и только затем запускать manual-sa в сервисе.

### Smoke-проверка FSM при debug-старте

Для быстрого само-проверочного запуска Core в debug-конфигурации можно вызвать:

- `var smoke = AikeMessageCodecSmoke.RunKe1Ke2EstablishedRoundTrip();`

Если `smoke.Success == false`, считать это деградацией: `smoke.Error` должен попасть в лог и инит прерывается/переводится в degraded mode.

При `success=true` использовать:

- `smoke.SessionId` для трассировки сессии,
- `smoke.FinalState` для контроля достижимости `Established`.

Для проверки границы `AIKE established -> ManualSaInstallPlan` можно вызывать:

- `AikeMessageCodecSmoke.RunEstablishedAndDeriveEspMaterial();`
- `AikeMessageCodecSmoke.RunEstablishedAndBuildManualSaInstallPlan();`
- `AikeMessageCodecSmoke.RunPlanFileBoundaryRoundTrip();` (полный boundary smoke с сериализацией и redacted summary).
- `AikeMessageCodecSmoke.RunDynamicEndpointPlanFileBoundaryRoundTrip();` (dynamic endpoint smoke: endpoint resolve -> plan -> plan-file v1 roundtrip).

Boundary contract:
- `Service` обычно вызывает `RunEstablishedAndBuildManualSaInstallPlan(...)` (или эквивалент),
- получает `ManualSaInstallPlanResult`,
- если `Success=true`, смотрит на `Validation.IsValid`, затем сериализует `plan` через `ManualSaPlanFileSerializer.ToKeyValuePlan(plan)` и передаёт текст в Manual-sa runner pipeline,
- если `Success=false`, может логировать `Error` и `Validation.Error` и не передавать plan в runner,
- `plan.ipsec_protocol` остаётся для IPsec (ESP/50), а `plan.traffic_selector_protocol` — для SA traffic selector (например `icmp`);
- `plan.profile` содержит ожидаемые key-lengths; если в будущем профиль поменяется, `Service` должен пересобрать/manual-sa параметры по этому профилю через свой собственный маппинг.
- никакой запуск `manual-sa` не выполняется внутри `AdaptiveClient.Core`.

Миграционный заметка для Service:
- старые вызовы `manual-sa --protocol` со значением `"50"` должны быть заменены на `plan.traffic_selector_protocol`;
- для IPsec-протокола в `manual-sa` использовать отдельный параметр/контекст, например `IPSEC_PROTOCOL=ESP(50)` в CLI-мире, а не `--protocol`;
- для direction-specific ключей:
  - `plan.InboundCipherKeyPlaceholder` → inbound direction,
  - `plan.OutboundCipherKeyPlaceholder` → outbound direction;
- длины ключей берутся из `plan.Profile` (в smoke для ручного профиля: 16/32).
- dynamic endpoint migration:
  - Service больше не должен считать `peer_id` равным IP;
  - Service хранит/получает endpoint records отдельно от identity trust;
  - перед установкой manual SA резолвит current local/remote endpoints через Core-compatible rules;
  - при IP update с большим `sequence` нужно rebuild plan и reinstall/rekey SA.

### Контракт Core → Service → manual-sa (plan-file v1)

`AdaptiveClient.Core` передаёт `ManualSaInstallPlan`, а затем сериализует его в ключ-значения:

```text
local=<ipv4/ipv6>
remote=<ipv4/ipv6>
selector_protocol=<icmp|any|number>
in_spi=<uint>
out_spi=<uint>
in_cipher_key=<hex>
in_auth_key=<hex>
out_cipher_key=<hex>
out_auth_key=<hex>
```

Сервис при передаче в `manual-sa`-runner:
- использует `selector_protocol` как L4/traffic selector для `manual-sa --protocol`;
- не подставляет `"50"` в это поле;
- `ESP`/`50` удерживается как отдельная IPsec-семантика из `plan.IpsecProtocol` (не в plan-file v1).
- передаёт key material по направлениям:
  - inbound-ключи → inbound SA,
  - outbound-ключи → outbound SA;
- `ToRedactedKeyValuePlan(...)` используется для безопасного логирования (в логах нет `in_*_key`, только `*_len`).
