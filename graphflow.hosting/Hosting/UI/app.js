import { readEvents } from './sse.mjs';
const $ = id => document.getElementById(id);
const pretty = value => JSON.stringify(value, null, 2);
let api, graph, busy = false, controller, total = 0, selectedEvent = false;
let nodes = new Map(), edges = new Map(), latest = null;
function status(text, kind = 'idle') { $('status').textContent = text; $('status').dataset.kind = kind; }
function error(text = '') { $('error').textContent = text; $('error').hidden = !text; }
function setBusy(value, streaming = false) {
    busy = value;
    for (const id of ['input', 'reset', 'stream', 'run']) $(id).disabled = value || !graph;
    $('refresh').disabled = value;
    $('graphs').disabled = value || !$('graphs').value;
    $('disconnect').hidden = !value || !streaming;
}
async function jsonResponse(response) {
    const value = await response.json().catch(() => null);
    if (!response.ok) throw new Error(value?.title || value?.error || `Request failed (${response.status}).`);
    return value;
}
function resetOutput() {
    total = 0; selectedEvent = false; latest = null; $('latest').hidden = true;
    $('events').replaceChildren(); $('event-count').textContent = '0 events';
    $('run-id').textContent = ''; $('state').textContent = 'Waiting for execution…';
    $('output-title').textContent = 'State & result';
    for (const item of [...nodes.values(), ...edges.values()]) item.classList.remove('active', 'visited');
}
async function selectGraph() {
    graph = null; setBusy(true); error();
    try {
        graph = await jsonResponse(await fetch(`${api}/graphs/${encodeURIComponent($('graphs').value)}`));
        $('graph-title').textContent = graph.displayName || graph.name;
        $('description').textContent = graph.description || 'A registered GraphFlow graph.';
        $('input').value = pretty(graph.exampleInput ?? {});
        $('input-help').textContent = graph.exampleInput == null ? 'No example provided. Use the schema to supply initial state.' : 'Edit the initial state before starting a run.';
        $('schema').textContent = pretty(graph.inputSchema);
        resetOutput(); $('state').textContent = 'Your result will appear here.';
        drawGraph(); status('Ready');
    } catch (ex) { error(ex.message); status('Unable to load graph', 'error'); }
    finally { setBusy(false); }
}
async function loadGraphs() {
    setBusy(true); error();
    try {
        const list = await jsonResponse(await fetch(`${api}/graphs`));
        $('graphs').replaceChildren(...list.map(g => new Option(g.displayName || g.name, g.name)));
        if (!list.length) {
            graph = null; $('graphs').add(new Option('No registered graphs', ''));
            $('graph-title').textContent = 'No graphs registered'; $('description').textContent = 'Register a graph in the host to begin.';
            $('graph').replaceChildren(); $('empty-map').hidden = false; status('No graphs'); return;
        }
        await selectGraph();
    } catch (ex) { error(ex.message); status('Connection failed', 'error'); }
    finally { setBusy(false); }
}
function svgElement(tag, attrs = {}, text) {
    const element = document.createElementNS('http://www.w3.org/2000/svg', tag);
    for (const [name, value] of Object.entries(attrs)) element.setAttribute(name, value);
    if (text != null) element.textContent = text;
    return element;
}
function selectable(element, label) {
    element.setAttribute('tabindex', '0'); element.setAttribute('role', 'button'); element.setAttribute('aria-label', label);
    const select = () => $('selection').textContent = label;
    element.addEventListener('click', select);
    element.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); select(); } });
}
function drawGraph() {
    const svg = $('graph'); svg.replaceChildren(); nodes = new Map(); edges = new Map();
    $('empty-map').hidden = graph.nodes.length > 0;
    $('counts').textContent = `${graph.nodes.length} nodes · ${graph.edges.length} edges`;
    $('topology-title').textContent = graph.displayName || graph.name;
    $('selection').textContent = 'Select a node or edge for its name. Edge highlights indicate evaluation, not a branch decision.';
    // Breadth-first levels handle cycles without recursively following back edges.
    const ranks = new Map(); const queue = [];
    if (graph.startNode) { ranks.set(graph.startNode, 0); queue.push(graph.startNode); }
    for (let index = 0; index < queue.length; index++) {
        for (const edge of graph.edges.filter(e => e.source === queue[index]))
            if (!ranks.has(edge.target)) { ranks.set(edge.target, ranks.get(edge.source) + 1); queue.push(edge.target); }
    }
    let last = Math.max(0, ...ranks.values());
    for (const node of graph.nodes) if (!ranks.has(node.name)) ranks.set(node.name, ++last);
    const levels = new Map();
    for (const node of graph.nodes) { const rank = ranks.get(node.name); if (!levels.has(rank)) levels.set(rank, []); levels.get(rank).push(node); }
    const width = Math.max(650, ...[...levels.values()].map(row => row.length * 200 + 100));
    const height = Math.max(280, (Math.max(0, ...ranks.values()) + 1) * 100 + 35);
    svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
    svg.style.minWidth = width > 850 ? `${width}px` : '0';
    // Preserve readability for large graphs by allowing scrolling rather than shrinking the entire map.
    svg.style.height = 'auto'; svg.style.minHeight = '0'; svg.style.aspectRatio = `${width} / ${height}`; svg.style.maxHeight = 'none';
    const defs = svgElement('defs'); const marker = svgElement('marker', { id: 'arrow', viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 6, markerHeight: 6, orient: 'auto-start-reverse' });
    marker.append(svgElement('path', { d: 'M 0 0 L 10 5 L 0 10 z', fill: 'context-stroke' })); defs.append(marker); svg.append(defs);
    const positions = new Map();
    for (const [rank, row] of levels) row.forEach((node, index) => positions.set(node.name, { x: width * (index + 1) / (row.length + 1), y: 55 + rank * 100 }));
    for (const edge of graph.edges) {
        const from = positions.get(edge.source), to = positions.get(edge.target); if (!from || !to) continue;
        let d;
        if (to.y > from.y) d = `M ${from.x} ${from.y + 25} C ${from.x + edge.order * 15} ${from.y + 65}, ${to.x} ${to.y - 65}, ${to.x} ${to.y - 25}`;
        else { const bend = Math.max(from.x, to.x) + 115 + edge.order * 18; d = `M ${from.x + 76} ${from.y} C ${bend} ${from.y + 55}, ${bend} ${to.y - 55}, ${to.x + 76} ${to.y}`; }
        const path = svgElement('path', { d, class: 'edge', 'marker-end': 'url(#arrow)' });
        const label = `${edge.name}: ${edge.source} → ${edge.target} (order ${edge.order})`;
        path.append(svgElement('title', {}, label)); selectable(path, label); svg.append(path); edges.set(edge.name, path);
    }
    for (const node of graph.nodes) {
        const { x, y } = positions.get(node.name);
        const group = svgElement('g', { class: 'node', transform: `translate(${x - 76},${y - 25})` });
        group.append(svgElement('rect', { width: 152, height: 50, rx: 9 }), svgElement('text', { x: 76, y: 23, 'text-anchor': 'middle' }, node.name.length > 20 ? node.name.slice(0, 19) + '…' : node.name), svgElement('text', { x: 76, y: 39, 'text-anchor': 'middle', class: 'small' }, node.name === graph.startNode ? 'START NODE' : 'NODE'));
        group.append(svgElement('title', {}, node.name)); selectable(group, `Node: ${node.name}${node.name === graph.startNode ? ' · Start node' : ''}`);
        svg.append(group); nodes.set(node.name, group);
    }
}
function onProgress(progress) {
    total++; $('event-count').textContent = `${total} events`;
    $('run-id').textContent = progress.runId;
    const item = progress.eventType.startsWith('Node') ? nodes.get(progress.objectName) : progress.eventType.startsWith('Edge') ? edges.get(progress.objectName) : null;
    if (item) {
        item.classList.toggle('active', progress.eventType.endsWith('Start'));
        if (progress.eventType.endsWith('Complete')) item.classList.add('visited');
    }
    latest = { title: 'Live state', value: progress.state }; $('latest').hidden = false;
    if (!selectedEvent) { $('output-title').textContent = 'Live state'; $('state').textContent = pretty(progress.state); }
    const li = document.createElement('li'); const button = document.createElement('button');
    for (const value of [progress.sequence, progress.eventType, progress.objectName]) { const span = document.createElement('span'); span.textContent = value; button.append(span); }
    button.addEventListener('click', () => { selectedEvent = true; $('output-title').textContent = `Snapshot · event ${progress.sequence}`; $('state').textContent = pretty(progress.state); });
    li.append(button); $('events').append(li); if ($('events').children.length > 500) $('events').firstElementChild.remove();
    $('events').scrollTop = $('events').scrollHeight;
}
function finish(result) {
    latest = { title: 'Final result', value: result }; $('latest').hidden = false; selectedEvent = false;
    $('run-id').textContent = result.runId; $('output-title').textContent = 'Final result'; $('state').textContent = pretty(result);
    for (const item of [...nodes.values(), ...edges.values()]) item.classList.remove('active');
    status(result.succeeded ? 'Completed' : 'Graph failed', result.succeeded ? 'success' : 'error');
    if (!result.succeeded) error(result.error || 'Graph execution failed.');
}
async function execute(streaming) {
    if (busy || !graph) return;
    error(); let input;
    try { input = JSON.parse($('input').value); } catch { error('Input is not valid JSON. Check it before running.'); $('input').focus(); return; }
    if (input === null) { error('Graph input must not be null.'); return; }
    setBusy(true, streaming); resetOutput(); status(streaming ? 'Streaming…' : 'Running…', 'running');
    controller = new AbortController(); let ended = false;
    try {
        const response = await fetch(`${api}/graphs/${encodeURIComponent(graph.name)}/runs${streaming ? '/stream' : ''}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input), signal: controller.signal
        });
        if (!streaming) {
            const result = await response.json();
            if (typeof result.succeeded !== 'boolean') throw new Error(result.title || `Request failed (${response.status}).`);
            finish(result); return;
        }
        if (!response.ok) await jsonResponse(response);
        if (!response.headers.get('content-type')?.includes('text/event-stream')) throw new Error('The server did not return an event stream.');
        for await (const event of readEvents(response.body)) {
            if (event.type === 'progress') onProgress(JSON.parse(event.data));
            if (event.type === 'end') { finish(JSON.parse(event.data)); ended = true; break; }
        }
        if (!ended) throw new Error('The stream closed without a final result. Execution may still be running.');
    } catch (ex) {
        const disconnected = controller.signal.aborted;
        status(disconnected ? 'Disconnected' : 'Request failed', 'error');
        error(disconnected ? 'Disconnected from the stream. This does not cancel graph execution.' : ex.message);
    } finally { controller = null; setBusy(false); }
}
$('graphs').addEventListener('change', selectGraph);
$('latest').addEventListener('click', () => {
    if (latest) {
        selectedEvent = false;
        $('output-title').textContent = latest.title;
        $('state').textContent = pretty(latest.value);
    }
});
$('refresh').addEventListener('click', loadGraphs);
$('reset').addEventListener('click', () => { $('input').value = pretty(graph.exampleInput ?? {}); error(); });
$('stream').addEventListener('click', () => execute(true)); $('run').addEventListener('click', () => execute(false));
$('disconnect').addEventListener('click', () => controller?.abort());
try { api = (await jsonResponse(await fetch('./config'))).apiPrefix; await loadGraphs(); }
catch (ex) { error(ex.message); status('Unable to connect', 'error'); }

