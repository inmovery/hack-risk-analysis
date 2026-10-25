# Aeza Risk — анализатор рисков оттока

Панель для маркетолога. Отвечает на три вопроса: **кому из клиентов нужно внимание**, **почему возникает риск** и **сколько денег
бизнес может потерять**. Расчёт идёт по аккаунтам, услугам и транзакциям и обновляется сам при изменении данных, весов модели и
с течением времени. Открытые вкладки панели обновляются без перезагрузки.

Продакшен: <https://inmovery.dev> (вход по логину и паролю), Swagger — <https://inmovery.dev/api/v1/docs>.

## Содержание

- [Запуск](#запуск)
- [Что в панели](#что-в-панели)
- [API](#api)
- [Как добавить признак](#как-добавить-признак)
- [Разработка и тесты](#разработка-и-тесты)
- [Развёртывание](#развёртывание)
- [Ограничения](#ограничения)
- [Как считается score](docs/scoring.md) — признаки, веса, уровни, деньги, пример расчёта
- [Архитектура](docs/architecture.md) — компоненты, данные, пересчёт, безопасность, развёртывание
- [OpenAPI-спецификация](contracts/openapi.json)

## Запуск

Нужен Docker с Compose v2.

1. Создайте `.env` из примера:

   ```bash
   cp .env.example .env
   ```

   Задайте `POSTGRES_PASSWORD`, `APP_DB_PASSWORD` и `BOOTSTRAP_ADMIN_PASSWORD` (не короче 12 символов). Пользователь только для
   чтения — `BOOTSTRAP_ANALYST_LOGIN` и `BOOTSTRAP_ANALYST_PASSWORD`.

2. Запустите:

   ```bash
   docker compose up --build -d
   ```

   Сначала стартует база, затем миграции, затем сервис. При пустой базе сервис загружает тестовые данные организаторов
   (`test_data_yandex/data.json`) и публикует первый расчёт. Перезапуск без `-v` данные и настройки не теряет.

3. Откройте <http://localhost:8080> и войдите логином и паролем администратора из `.env`.

### Стенд для разработки

```bash
docker compose -f docker-compose.local.yaml up --build -d
```

| Сервис | Адрес |
| --- | --- |
| Панель | http://localhost:8080 |
| API и Swagger | http://localhost:8081/api/v1/docs |
| Prometheus | http://localhost:9090 |
| Grafana | http://localhost:3000 |

Не запускайте одновременно с `docker-compose.yml`: совпадают порты.

Симулятор источника присылает поток событий, как настоящая система-источник. Данные симулятора помечаются как синтетика. Нужен
`INGEST_TOKEN` в `.env`.

```bash
docker compose -f docker-compose.local.yaml --profile demo up -d source-sim
```

## Что в панели

- **Обзор** — выручка под риском, плановые продления и непокрытая часть на 30 дней; клиенты по уровням риска; три типа оттока:
  непродление, уход целиком, снижение платежей; прогноз продлений по неделям и факт из журнала событий.
- **Клиенты** — рейтинг с ID, email, балансом, суммой пополнений, score, уровнем, главной причиной и суммой под риском. Сортировка
  в обе стороны по ID, email, балансу, пополнениям, score и сумме под риском; фильтры, поиск, выгрузка в CSV.
- **Карточка клиента** — причины риска с вкладом каждого признака, расчёт денег по шагам, услуги и транзакции, история событий.
- **Модель** (администратор) — веса признаков, пороги уровней, горизонт, окно срочности. Предпросмотр без сохранения, история
  ревизий. Изменение пересчитывает результат и сохраняется после перезапуска.
- **Источник** — загрузка тестовых данных, импорт истории событий, состояние потока и журнал событий.

## API

Все маршруты, кроме `/metrics` и Swagger, требуют входа. Изменяющие запросы требуют заголовок `X-CSRF-TOKEN`
(выдаётся `GET /api/v1/auth/csrf`).

| Метод | Путь | Назначение |
| --- | --- | --- |
| POST | `/api/v1/auth/login` | Вход по логину и паролю |
| GET | `/api/v1/auth/me` | Текущий пользователь |
| POST | `/api/v1/auth/logout` | Выход |
| GET | `/api/v1/customers` | Рейтинг: `sortBy`, `sortDirection`, `riskLevel`, `search`, `page`, `pageSize` |
| GET | `/api/v1/customers/{id}` | Карточка клиента: услуги, признаки, причины, деньги |
| GET | `/api/v1/analytics/summary` | Сводка: уровни риска, деньги, прогноз |
| GET | `/api/v1/model/settings` | Действующие настройки модели |
| PATCH | `/api/v1/model/settings` | Изменение настроек → `202` и пересчёт |
| PUT | `/api/v1/source/snapshot` | Загрузка тестовых данных (полная замена) → `202` и пересчёт |
| GET | `/metrics` | Метрики Prometheus: клиенты по уровням, деньги, номер расчёта |
| GET | `/api/v1/docs` | Swagger UI |

Дополнительно: приём событий `POST /api/v1/source/events`, импорт истории `POST /api/v1/source/events/import`, выгрузка
`GET /api/v1/export/customers`, предпросмотр модели `POST /api/v1/model/preview`, статус теневой ML-модели `GET /api/v1/ml/status`.
Полный список — в Swagger и [contracts/openapi.json](contracts/openapi.json).

Пример загрузки тестовых данных:

```bash
curl -X PUT "http://localhost:8080/api/v1/source/snapshot?sourceAsOf=2026-10-19T15:00:00Z" \
  -H "Authorization: Bearer $INGEST_TOKEN" -H "Content-Type: application/json" \
  --data-binary @test_data_yandex/data.json
```

## Как добавить признак

1. Создайте класс в `src/Aeza.Risk.Core/Features/`:

   ```csharp
   public sealed class LowReplenishmentFeature : IRiskFeature
   {
       public string Key => "low_replenishment";
       public string Title => "Мало пополнений относительно цены";
       public string Description => "1, если всего пополнено меньше трёх оплат услуги";
       public decimal DefaultWeight => 10m;

       public FeatureValue Evaluate(ServiceContext ctx)
       {
           if (ctx.Account.TotalReplenished is not { } total || ctx.Service.Price is not { } price)
               return FeatureValue.Missing("Нет totalReplenished или цены", "account.totalReplenished", "price");
           return total < 3 * price
               ? FeatureValue.Of(1m, $"{ctx.Service.Name}: пополнено {Fmt.Eur(total)} — меньше трёх оплат по {Fmt.Eur(price)}")
               : FeatureValue.Of(0m, $"{ctx.Service.Name}: пополнений достаточно");
       }
   }
   ```

2. Добавьте `new LowReplenishmentFeature()` в `FeatureCatalog.All`.
3. Добавьте тест в `test/Aeza.Risk.UnitTests`.

Редактор весов, причины, деньги и API подхватят признак сами. Пока администратор не задаст вес, у нового признака вес 0.

## Разработка и тесты

```bash
dotnet test --project test/Aeza.Risk.UnitTests
```

```bash
dotnet test --project test/Aeza.Risk.IntegrationTests
```

Интеграционным тестам нужен Docker: Testcontainers поднимает PostgreSQL.

```bash
cd web && npm ci && npm run typecheck && npm test
```

```bash
cd web && npx playwright install chromium && npx playwright test
```

Браузерные тесты идут против запущенного стенда и берут логин и пароль из `../.env`.

Без Docker: PostgreSQL и `dotnet run --project src/Aeza.Risk.Web` (http://localhost:5080, миграции применяются при старте),
панель — `npm run dev` в `web/`.

## Развёртывание

- `docker compose --profile tls up -d` — nginx с HTTPS (задайте `DOMAIN`), `--profile observability` — Prometheus и Grafana.
- Kubernetes (k3s): Helm-чарты в `deploy/helm`, значения для продакшена — `deploy/k8s/values/production`.
- CI — `.gitlab-ci.yml`: сборка образов, выкатка вручную, откат.
- Смена пароля: `docker compose run --rm -e NEW_PASSWORD='…' migrator set-password admin` (сессии пользователя отзываются).

## Ограничения

- Score — индекс приоритета, а не вероятность ухода; веса по умолчанию экспертные и меняются в панели.
- В тестовых данных нет истории статусов и дат удаления, поэтому прошлый отток не рисуется. Факты появляются из потока событий
  после его подключения.
- Транзакции тестового набора созданы вместе с услугами, поэтому тренд платежей на нём почти везде «мало истории».
- Шесть услуг `prolong_wait` в наборе просрочены больше чем на 5 дней. Они помечены флагом качества данных, статус не переписывается.
- По умолчанию оценка идёт на текущее время, а тестовые данные датированы 19.10.2026. Режим «на момент выгрузки данных» включается
  в настройках модели.
- Теневая ML-модель пока проверена только на синтетических данных симулятора и на score не влияет.
