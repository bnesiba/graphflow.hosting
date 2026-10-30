// Incremental SSE parser: chunks may split UTF-8 characters, lines, or frames.
export async function* readEvents(body) {
    if (!body) throw new Error('Streaming is not supported by this response.');
    const reader = body.getReader();
    const decoder = new TextDecoder();
    let buffer = '', eventType = 'message', data = [];
    function line(value) {
        if (value === '') {
            const event = data.length ? { type: eventType, data: data.join('\n') } : null;
            eventType = 'message'; data = [];
            return event;
        }
        if (value.startsWith(':')) return null;
        const colon = value.indexOf(':');
        const field = colon < 0 ? value : value.slice(0, colon);
        let text = colon < 0 ? '' : value.slice(colon + 1);
        if (text.startsWith(' ')) text = text.slice(1);
        if (field === 'event') eventType = text;
        if (field === 'data') data.push(text);
        return null;
    }
    try {
        while (true) {
            const { value, done } = await reader.read();
            buffer += decoder.decode(value, { stream: !done });
            let at;
            while ((at = buffer.search(/[\r\n]/)) >= 0) {
                if (!done && buffer[at] === '\r' && at === buffer.length - 1) break;
                const width = buffer[at] === '\r' && buffer[at + 1] === '\n' ? 2 : 1;
                const event = line(buffer.slice(0, at));
                buffer = buffer.slice(at + width);
                if (event) yield event;
            }
            // An unterminated frame is deliberately discarded, as required by SSE.
            if (done) break;
        }
    } finally {
        await reader.cancel().catch(() => {});
        reader.releaseLock();
    }
}
