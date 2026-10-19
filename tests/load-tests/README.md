# Нагрузочное тестирование ChatFoundry

Нагрузочное тестирование полного цикла обработки сообщений:
`k6 (клиенты Telegram)` -> `TelegramService` -> `Kafka (bot.message.incoming)` -> `WorkflowService & ClientService` -> `Kafka (bot.message.outgoing)` -> `TelegramService` -> `TelegramMock`.

## Архитектура стенда

* **`TelegramMock`** (`tests/Mocks/TelegramMock`): Легковесный mock-сервер API Telegram на ASP.NET Core Minimal API. Логирует все входящие `sendMessage`, считает метрики, позволяет симулировать сетевые задержки и проверять сквозную доставку (`GET /mock/stats`, `POST /mock/verify`).
* **`ChatFoundry.LoadTests`** (`tests/ChatFoundry.LoadTests`): xUnit проект тестов для .NET/Rider. Позволяет запустить нагрузочный тест прямо из IDE по кнопке "Run Test" или через `dotnet test`.
* **`tests/load-tests/scenarios/branching-dialog.js`**: Сценарий k6 с ветвлением:
  1. Пользователь отправляет `/start`.
  2. Бот присылает опрос с кнопками (Ask Action) «Опция А» и «Опция Б».
  3. Пользователь выбирает одну из опций через `callback_query`.
  4. Движок воркфлоу идет по соответствующей ветке графа и шлет подтверждающее сообщение.
* **`tests/load-tests/seed/seed-data.sql`**: SQL-скрипт предзаполнения базы данных (тестовый канал, бот, граф воркфлоу с нодами Start, Ask, Message A, Message B).
* **`deployment/docker-compose.loadtest.yml`**: Полностью изолированный Docker-стенд со всеми реальными базами (Postgres, Redis, Kafka) и сервисами (`workflow-service`, `client-service`, `telegram-service`, `telegram-mock`, `k6`).

---

## Способы запуска

### Способ 1: Кнопкой из Rider / Visual Studio (C# xUnit Runner)
1. Откройте решение `ChatFoundry.sln` в Rider или Visual Studio.
2. В обозревателе тестов найдите проект `ChatFoundry.LoadTests`.
3. Нажмите зеленую кнопку **Run Test** у теста `Run_BranchingDialog_LoadTest_In_Docker`.
   * Тест сам выполнит сборку и запуск контейнеров через `docker compose`.
   * Выполнит k6 сценарий нагрузки.
   * Проверит SLA и сквозную доставку в `TelegramMock`.

Или из терминала:
```bash
dotnet test tests/ChatFoundry.LoadTests/ChatFoundry.LoadTests.csproj
```

---

### Способ 2: Запуск в Docker напрямую
1. **Поднять сервисы стенда**:
   ```bash
   docker compose -f deployment/docker-compose.loadtest.yml up -d --build
   ```
2. **Запустить нагрузочный сценарий k6**:
   ```bash
   docker compose -f deployment/docker-compose.loadtest.yml run --rm k6
   ```
3. **Проверить сверку сквозной доставки**:
   ```bash
   # Проверить статистику TelegramMock:
   curl http://localhost:8085/mock/stats
   ```
4. **Остановить стенд после тестирования**:
   ```bash
   docker compose -f deployment/docker-compose.loadtest.yml down
   ```

---

### Способ 3: Запуск k6 локально с хост-машины (для быстрой отладки JS)
Если на машине установлен k6:
```bash
# Поднимаем сервисы:
docker compose -f deployment/docker-compose.loadtest.yml up -d

# Запускаем локальный k6:
k6 run --env BASE_URL=http://localhost:5040 --env MOCK_URL=http://localhost:8085 tests/load-tests/scenarios/branching-dialog.js
```
