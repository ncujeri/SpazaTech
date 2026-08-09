// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
self.addEventListener('fetch', () => { });

// The dev worker does not cache, so it has no real update flow. It still honours the
// index.html "Update now" button's SKIP_WAITING message: if the browser is transitioning
// off a previously-installed published worker, this lets the click activate and reload
// rather than silently doing nothing.
self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') {
        self.skipWaiting();
    }
});
