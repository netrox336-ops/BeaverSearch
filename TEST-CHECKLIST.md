# BeaverSearch v0.4.1 FixSteamID — test checklist

## Build
- [ ] `START.bat`: STATIC-PREFLIGHT PASS.
- [ ] HOTFIX-PREFLIGHT PASS.
- [ ] `dotnet build` = 0 errors.
- [ ] MainWindow запускается без XamlParseException.

## Resolver / batches
- [ ] yooma.su + CYBERSHOKE дают exact SteamID64 без угадывания по нику.
- [ ] В UI только текущий batch до 10 community servers, а не 1000+ строк.
- [ ] Следующий batch стартует только после полного завершения текущих player tasks.
- [ ] Player valuation concurrency = 2; raw Steam inventory gate = 1.
- [ ] Chromium probe = 1, cache 75s, compact DOM, BelowNormal priority.

## Steam inventory presence v6
- [ ] Обычная profile inventory page парсится через `g_rgAppContextData`.
- [ ] Если `asset_count = 0` для игры, JSON inventory этой игры не запрашивается.
- [ ] Presence cache = 20 минут.
- [ ] Ошибка presence page не считается пустым inventory: выполняется обычный raw check.

## Steam raw inventory v6
- [ ] Только modern endpoint `/inventory/<SteamID>/<appid>/2?count=2000`.
- [ ] Legacy `/profiles/<SteamID>/inventory/json/...` полностью отсутствует.
- [ ] Raw requests сериализованы и идут с normal spacing около 10 секунд.
- [ ] После throttle spacing не меньше 15 секунд.
- [ ] 429 включает cooldown минимум 90 секунд; 403 — минимум 60 секунд.
- [ ] После 403/429 нет immediate retry.
- [ ] `HTTP 401 + empty/null body` считается доступным пустым app inventory, а не auth/temp error.
- [ ] Modern `assets/descriptions` и пагинация `more_items + last_assetid` корректны.
- [ ] Явный private inventory остаётся inaccessible.

## Pricing v6
- [ ] CS2 bulk source: SkinCash; Dota bulk source: market.dota2.net.
- [ ] Skinport — secondary provider и не валит весь valuation при 403/5xx.
- [ ] Steam Market — только last-resort fallback максимум для 4 имён.
- [ ] Steam Market gate=1, pacing около 6 секунд, positive cache 30 минут.
- [ ] Price source failure не записывает ложный успешный `0 ₽`.

## Results / cache
- [ ] Валидный CS2 MATCH сохраняется даже при temporary failure другой игры.
- [ ] Partial result не записывается в 24h successful cache.
- [ ] Пустой Rust/Dota inventory отображается как `0 items`, а не `401 Unauthorized`.
- [ ] `PriceEngineVersion = 6`; old SteamChecks очищаются один раз, SteamID/name mappings сохраняются.
- [ ] При CS2 filter `1..30000` подходящая ненулевая CS2 valuation попадает в Results.

## Regression по последнему runtime-логу
- [ ] Больше нет `modern: ... | legacy: ...`.
- [ ] Больше нет `partial valuation — Rust: HTTP 401 Unauthorized` для пустого Rust inventory.
- [ ] Если появляется 429, дальнейшие raw inventory requests реально ждут cooldown.
- [ ] Следующая десятка серверов не стартует поверх незавершённых inventory tasks.

## Repository / GUI
- [ ] BeaverSearch icon и sidebar background отображаются корректно.
- [ ] В репозитории нет `bin/`, `obj/`, `.vs/`, `.tmp`.
- [ ] `.github/workflows` отсутствует; GitHub Actions не используется.
