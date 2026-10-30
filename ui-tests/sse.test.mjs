import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readEvents } from '../GraphFlow.Hosting.AspNetCore/UI/sse.mjs';
const encode = new TextEncoder();
async function parse(chunks) {
    const body = new ReadableStream({ start(c) { for (const chunk of chunks) c.enqueue(chunk); c.close(); } });
    const result = []; for await (const event of readEvents(body)) result.push(event); return result;
}
test('arbitrary chunk boundaries preserve UTF-8, CRLF, multi-line data and terminal event', async () => {
    const bytes = encode.encode(': heartbeat\r\nevent: progress\r\ndata: {"name":"é◇",\r\ndata: "value":1}\r\n\r\nevent: end\ndata: {"succeeded":true}\n\n');
    for (let split = 0; split <= bytes.length; split++) {
        const result = await parse([bytes.slice(0, split), bytes.slice(split)]);
        assert.equal(result.length, 2); assert.equal(result[0].type, 'progress');
        assert.deepEqual(JSON.parse(result[0].data), { name: 'é◇', value: 1 });
        assert.equal(result[1].type, 'end');
    }
    assert.equal((await parse([...bytes].map(b => Uint8Array.of(b)))).length, 2);
});
test('ignores comments, unknown fields and incomplete final frames', async () => {
    assert.deepEqual(await parse([encode.encode(':hello\n\nid: 1\nevent: progress\ndata: one\n\ndata: unfinished')]), [{ type: 'progress', data: 'one' }]);
});
test('bare CR framing and empty data are supported', async () => {
    assert.deepEqual(await parse([encode.encode('data:\r\rdata: second\r\r')]), [{ type: 'message', data: '' }, { type: 'message', data: 'second' }]);
});
test('breaking after end cancels the reader', async () => {
    let canceled = false;
    const stream = new ReadableStream({ start(c) { c.enqueue(encode.encode('event: end\ndata: {}\n\n')); }, cancel() { canceled = true; } });
    for await (const event of readEvents(stream)) { assert.equal(event.type, 'end'); break; }
    assert.equal(canceled, true);
});

