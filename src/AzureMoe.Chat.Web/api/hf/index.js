const SKIP = new Set([
    'content-encoding',
    'content-security-policy',
    'transfer-encoding',
    'connection',
    'content-length', // let the runtime compute from actual body
]);

module.exports = async function (context, req) {
    try {
        const hfPath = context.bindingData.path ?? '';

        let search = '';
        try { search = new URL(req.url).search; } catch {}

        const target = `https://huggingface.co/${hfPath}${search}`;
        context.log(`[hf] ${req.method} ${target}`);

        const init = { method: req.method, headers: {}, redirect: 'follow' };
        const range = req.headers['range'];
        if (range) init.headers['Range'] = range;

        const upstream = await fetch(target, init);
        context.log(`[hf] upstream ${upstream.status}, content-length=${upstream.headers.get('content-length')}`);

        const headers = {
            'access-control-allow-origin': '*',
            'cross-origin-resource-policy': 'cross-origin',
        };
        for (const [key, value] of upstream.headers) {
            if (!SKIP.has(key.toLowerCase())) headers[key] = value;
        }
        context.log(`[hf] headers built`);

        let body = null;
        if (req.method !== 'HEAD') {
            context.log(`[hf] reading body...`);
            const ab = await upstream.arrayBuffer();
            context.log(`[hf] body read: ${ab.byteLength} bytes`);
            body = Buffer.from(ab);
            context.log(`[hf] buffer created`);
        }

        context.res = { status: upstream.status, headers, body };
        context.log(`[hf] done`);

    } catch (err) {
        // context.log (not .error) so it appears in Application Insights traces
        context.log(`[hf] ERROR ${err.name}: ${err.message}\n${err.stack}`);
        context.res = {
            status: 502,
            headers: {
                'content-type': 'text/plain; charset=utf-8',
                'access-control-allow-origin': '*',
            },
            body: `[hf proxy error]\n${err.name}: ${err.message}\n\n${err.stack}`,
        };
    }
};
