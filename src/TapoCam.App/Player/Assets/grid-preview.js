(() => {
    const videoOnly = tapoGrid || new URLSearchParams(location.search).get('media') === 'video';
    if (!videoOnly) return;
    const start = () => {
        if (!document.body || !document.head) return;
        if (tapoGrid) {
            const style = document.createElement('style');
            style.textContent = `video-stream { min-width: 0; flex: 1 1 100% !important; }
                                 video-stream > .info { display: none !important; }`;
            document.head.append(style);
        }
        const update = () => document.querySelectorAll('video-stream').forEach(player => {
            player.media = 'video';
            const video = player.querySelector('video');
            if (video) {
                video.muted = true;
                if (tapoGrid && video.controls) video.controls = false;
            }
        });
        new MutationObserver(update).observe(document.body, {
            childList: true, subtree: true, attributes: true, attributeFilter: ['controls']
        });
        update();
    };
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
        start();
    }
})();
