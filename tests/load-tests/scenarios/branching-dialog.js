import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';

// Custom metrics
const startRequestDuration = new Trend('start_request_duration');
const callbackRequestDuration = new Trend('callback_request_duration');
const failedRequests = new Counter('failed_requests');
const completedDialogs = new Counter('completed_dialogs');

// Configurable environment variables
const BASE_URL = __ENV.BASE_URL || 'http://localhost:5040';
const CHANNEL_ID = __ENV.CHANNEL_ID || '00000000-0000-0000-0000-000000000001';
const MOCK_URL = __ENV.MOCK_URL || 'http://localhost:8085';

export const options = {
    stages: [
        { duration: '10s', target: 50 },   // Разгон: 50 пользователей
        { duration: '30s', target: 200 },  // Планка: 200 одновременных пользователей
        { duration: '10s', target: 0 },    // Завершение
    ],
    thresholds: {
        'http_req_failed': ['rate<0.01'],             // Ошибок меньше 1%
        'start_request_duration': ['p(95)<150'],      // 95% запросов быстрее 150мс
        'callback_request_duration': ['p(95)<150'],
    },
};

export function setup() {
    console.log(`[k6] Starting Load Test for ChatFoundry...`);
    console.log(`[k6] Target Webhook: ${BASE_URL}/hook/${CHANNEL_ID}`);
    console.log(`[k6] Telegram Mock: ${MOCK_URL}`);

    // Ожидание готовности telegram-service
    console.log(`[k6] Waiting for telegram-service at ${BASE_URL}...`);
    let ready = false;
    for (let i = 0; i < 30; i++) {
        try {
            const ping = http.get(`${BASE_URL}/`);
            if (ping.status === 200) {
                ready = true;
                console.log(`[k6] telegram-service is online and ready!`);
                break;
            }
        } catch (e) {
            // wait
        }
        sleep(1);
    }
    if (!ready) {
        console.warn(`[k6] Warning: telegram-service did not respond 200 within 30s`);
    }

    // Очищаем счетчики мока перед началом теста
    try {
        http.del(`${MOCK_URL}/mock/reset`);
    } catch (e) {
        console.warn(`[k6] Could not reset mock stats (is mock reachable?): ${e}`);
    }
}

export default function () {
    // Формируем уникальный chatId для каждого пользователя и каждой итерации:
    // Это гарантирует честное распределение по партициям Kafka ($channelId:$chatId)
    const chatId = 1000000 + (__VU * 10000) + __ITER;
    const updateId = 5000000 + (__VU * 10000) + (__ITER * 2);
    const headers = { 'Content-Type': 'application/json' };
    const webhookUrl = `${BASE_URL}/hook/${CHANNEL_ID}`;

    // --- ШАГ 1: Отправка команды /start ---
    const startPayload = JSON.stringify({
        update_id: updateId,
        message: {
            message_id: __ITER + 1,
            chat: { id: chatId, type: 'private' },
            from: {
                id: chatId,
                first_name: `User_${__VU}`,
                username: `user_${__VU}`
            },
            text: '/start',
            date: Math.floor(Date.now() / 1000)
        }
    });

    const startRes = http.post(webhookUrl, startPayload, { headers });
    startRequestDuration.add(startRes.timings.duration);

    const startOk = check(startRes, {
        'start: status is 200': (r) => r.status === 200,
    });

    if (!startOk) {
        failedRequests.add(1);
        sleep(1);
        return;
    }

    // Имитация чтения пользователем сообщения с кнопками (1 секунда)
    sleep(1);

    // --- ШАГ 2: Выбор ветки (нажатие кнопки: Option A или Option B) ---
    // Четные пользователи выбирают 'option_a', нечетные — 'option_b'
    const chosenOption = (__VU % 2 === 0) ? 'option_a' : 'option_b';

    const callbackPayload = JSON.stringify({
        update_id: updateId + 1,
        callback_query: {
            id: `cb_${chatId}_${__ITER}`,
            from: {
                id: chatId,
                first_name: `User_${__VU}`,
                username: `user_${__VU}`
            },
            chat_instance: '1',
            data: chosenOption
        }
    });

    const callbackRes = http.post(webhookUrl, callbackPayload, { headers });
    callbackRequestDuration.add(callbackRes.timings.duration);

    const callbackOk = check(callbackRes, {
        'callback: status is 200': (r) => r.status === 200,
    });

    if (!callbackOk) {
        failedRequests.add(1);
        sleep(1);
    } else {
        completedDialogs.add(1);
    }

    // Пауза перед следующим циклом диалога
    sleep(1);
}

export function teardown() {
    console.log(`[k6] Load Test completed. Waiting for Kafka consumers to drain backlog...`);
    sleep(10); // Даем консьюмерам дочитать остаточные сообщения в Kafka под стрессовой нагрузкой

    try {
        const statsRes = http.get(`${MOCK_URL}/mock/stats`);
        if (statsRes.status === 200) {
            console.log(`[k6] Mock Stats: ${statsRes.body}`);
        }
    } catch (e) {
        console.warn(`[k6] Could not query mock stats: ${e}`);
    }
}

export function handleSummary(data) {
    const reportHtml = generateHtmlReport(data);
    return {
        '/scripts/reports/report.html': reportHtml,
        '/scripts/reports/summary.json': JSON.stringify(data, null, 2),
    };
}

function generateHtmlReport(data) {
    const metrics = data.metrics || {};
    const httpReqs = metrics.http_reqs ? metrics.http_reqs.values.count : 0;
    const httpFailRate = metrics.http_req_failed ? (metrics.http_req_failed.values.rate * 100).toFixed(2) : 0;
    const avgDuration = metrics.http_req_duration ? metrics.http_req_duration.values.avg.toFixed(1) : 0;
    const p95Duration = metrics.http_req_duration ? metrics.http_req_duration.values['p(95)'].toFixed(1) : 0;
    const vus = metrics.vus_max ? metrics.vus_max.values.value : 0;
    const completed = metrics.completed_dialogs ? metrics.completed_dialogs.values.count : 0;

    let rows = '';
    for (const [name, m] of Object.entries(metrics)) {
        if (!m.values) continue;
        const vals = m.values;
        const avg = vals.avg !== undefined ? vals.avg.toFixed(1) : '-';
        const min = vals.min !== undefined ? vals.min.toFixed(1) : '-';
        const med = vals.med !== undefined ? vals.med.toFixed(1) : '-';
        const max = vals.max !== undefined ? vals.max.toFixed(1) : '-';
        const p90 = vals['p(90)'] !== undefined ? vals['p(90)'].toFixed(1) : '-';
        const p95 = vals['p(95)'] !== undefined ? vals['p(95)'].toFixed(1) : '-';
        const count = vals.count !== undefined ? vals.count : (vals.value !== undefined ? vals.value : '-');

        rows += `<tr>
            <td class="metric-name">${name}</td>
            <td>${count}</td>
            <td>${min}</td>
            <td>${avg}</td>
            <td>${med}</td>
            <td>${p90}</td>
            <td class="highlight">${p95}</td>
            <td>${max}</td>
        </tr>`;
    }

    return `<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <title>ChatFoundry - Load Test Report</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; }
        body { background: #0f172a; color: #f8fafc; padding: 30px; line-height: 1.5; }
        .container { max-width: 1200px; margin: 0 auto; }
        .header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 25px; border-bottom: 1px solid #334155; padding-bottom: 15px; }
        .title { font-size: 26px; font-weight: bold; color: #38bdf8; }
        .subtitle { font-size: 14px; color: #94a3b8; }
        .badge { background: #059669; color: white; padding: 6px 14px; border-radius: 20px; font-size: 14px; font-weight: bold; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 15px; margin-bottom: 30px; }
        .card { background: #1e293b; border: 1px solid #334155; border-radius: 10px; padding: 18px; text-align: center; }
        .card .label { font-size: 13px; color: #94a3b8; margin-bottom: 6px; text-transform: uppercase; }
        .card .value { font-size: 24px; font-weight: bold; color: #f1f5f9; }
        .card .value.good { color: #34d399; }
        .card .value.warn { color: #f87171; }
        .section-title { font-size: 18px; font-weight: bold; margin-bottom: 15px; color: #e2e8f0; }
        table { width: 100%; border-collapse: collapse; background: #1e293b; border-radius: 8px; overflow: hidden; margin-bottom: 30px; }
        th, td { padding: 12px 16px; text-align: left; border-bottom: 1px solid #334155; font-size: 14px; }
        th { background: #1e293b; color: #94a3b8; font-weight: 600; text-transform: uppercase; font-size: 12px; }
        tr:hover { background: #243248; }
        .metric-name { font-weight: bold; color: #38bdf8; font-family: monospace; }
        .highlight { font-weight: bold; color: #fbbf24; }
        .footer { text-align: center; color: #64748b; font-size: 13px; margin-top: 40px; }
    </style>
</head>
<body>
    <div class="container">
        <div class="header">
            <div>
                <div class="title">ChatFoundry Load Test Report</div>
                <div class="subtitle">Сценарий: Диалог с ветвлениями (Start -> Ask Buttons -> Choice Reply)</div>
            </div>
            <div class="badge">TEST COMPLETED</div>
        </div>

        <div class="cards">
            <div class="card">
                <div class="label">Всего HTTP вызовов</div>
                <div class="value">${httpReqs}</div>
            </div>
            <div class="card">
                <div class="label">Ошибок HTTP</div>
                <div class="value ${httpFailRate > 0 ? 'warn' : 'good'}">${httpFailRate}%</div>
            </div>
            <div class="card">
                <div class="label">Ср. время вебхука</div>
                <div class="value">${avgDuration} ms</div>
            </div>
            <div class="card">
                <div class="label">95-й перцентиль</div>
                <div class="value">${p95Duration} ms</div>
            </div>
            <div class="card">
                <div class="label">Пик пользователей (VUs)</div>
                <div class="value">${vus}</div>
            </div>
            <div class="card">
                <div class="label">Завершенных диалогов</div>
                <div class="value good">${completed}</div>
            </div>
        </div>

        <div class="section-title">Детальные метрики производительности</div>
        <table>
            <thead>
                <tr>
                    <th>Метрика</th>
                    <th>Count</th>
                    <th>Min (ms)</th>
                    <th>Avg (ms)</th>
                    <th>Med (ms)</th>
                    <th>p(90) (ms)</th>
                    <th>p(95) (ms)</th>
                    <th>Max (ms)</th>
                </tr>
            </thead>
            <tbody>
                ${rows}
            </tbody>
        </table>

        <div class="footer">
            ChatFoundry Load Testing System &bull; Generated at ${new Date().toISOString()}
        </div>
    </div>
</body>
</html>`;
}
