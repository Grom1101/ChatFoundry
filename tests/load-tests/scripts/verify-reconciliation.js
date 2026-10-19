import http from 'k6/http';
import { check } from 'k6';

const MOCK_URL = __ENV.MOCK_URL || 'http://localhost:8085';
const EXPECTED_COUNT = parseInt(__ENV.EXPECTED_COUNT || '0', 10);

export const options = {
    vus: 1,
    iterations: 1,
};

export default function () {
    console.log(`\n================ RECONCILIATION VERIFICATION ================`);
    console.log(`Checking Telegram Mock at: ${MOCK_URL}`);

    const res = http.get(`${MOCK_URL}/mock/stats`);
    check(res, {
        'stats endpoint responds 200': (r) => r.status === 200,
    });

    if (res.status !== 200) {
        console.error(`[FAIL] Telegram Mock is unreachable or returned status ${res.status}`);
        return;
    }

    const stats = JSON.parse(res.body);
    console.log(`\n--- Telegram Mock Results ---`);
    console.log(`Total Outgoing Messages Received by Mock: ${stats.totalReceived}`);
    console.log(`Unique Chat IDs:                          ${stats.uniqueChats}`);
    console.log(`Breakdown by Method:                      ${JSON.stringify(stats.byMethod)}`);

    if (EXPECTED_COUNT > 0) {
        const diff = stats.totalReceived - EXPECTED_COUNT;
        console.log(`\n--- Loss Analysis ---`);
        console.log(`Expected Count: ${EXPECTED_COUNT}`);
        console.log(`Actual Count:   ${stats.totalReceived}`);
        if (diff === 0) {
            console.log(`\x1b[32m[STATUS: SUCCESS] 100% messages delivered. Zero loss!\x1b[0m`);
        } else if (diff < 0) {
            console.log(`\x1b[31m[STATUS: FAILED] Message loss detected! Missing ${Math.abs(diff)} messages!\x1b[0m`);
        } else {
            console.log(`\x1b[33m[STATUS: WARNING] Received ${diff} extra/duplicate messages.\x1b[0m`);
        }
    }
    console.log(`=============================================================\n`);
}
