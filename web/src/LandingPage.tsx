export function LandingPage() {
  return <main className="atlas-landing">
    <nav className="landing-nav" aria-label="Landing navigation">
      <a className="landing-wordmark" href="/" aria-label="Archie home"><span>A</span>Archie</a>
      <div>
        <a href="#product">Product</a>
        <a href="#how-it-works">How it works</a>
        <a className="landing-nav-cta" href="/app">Get started</a>
      </div>
    </nav>

    <section className="landing-hero" id="product">
      <div className="landing-copy">
        <p className="landing-eyebrow"><i /> Local architecture context, grounded in your code</p>
        <h1>Understand your repository.<br /><em>Follow every connection.</em></h1>
        <p className="landing-lede">Archie scans locally installed language analyzers into focused dependency and inferred-runtime maps, with source evidence behind every relationship.</p>
        <div className="landing-actions">
          <a className="landing-primary-cta" href="/app">Get started <span>→</span></a>
          <a className="landing-secondary-cta" href="#how-it-works">See how it works</a>
        </div>
        <div className="landing-trust"><span>✓ Grounded in canonical evidence</span><span>✓ Built for complex systems</span></div>
      </div>

      <div className="landing-product-frame" aria-label="Archie dependency map preview">
        <div className="landing-frame-bar"><span><b>archie</b> / book-retail</span><small><i /> Canonical graph</small></div>
        <div className="landing-frame-body">
          <aside>
            <span className="landing-spark">⌘</span>
            <small>DEPENDENCY MAP</small>
            <strong>Checkout Service</strong>
            <p>Inspect the services, events, data stores, and source evidence connected to checkout.</p>
            <div><span>✓ Canonical evidence</span><span>Local only</span></div>
            <button><i>→</i><span><b>Inspect evidence</b><small>Open exact source locations</small></span><em>›</em></button>
          </aside>
          <section className="landing-map-preview">
            <header><span>DEPENDENCY MAP</span><b>How the codebase is connected</b></header>
            <div className="landing-map-grid">
              <div className="landing-node checkout"><small>MODULE</small><b>Checkout</b><span>2 components</span></div>
              <div className="landing-node event"><small>EVENT</small><b>order.submitted</b><span>Kafka topic</span></div>
              <div className="landing-node ordering"><small>MODULE</small><b>Ordering</b><span>3 components</span></div>
              <svg viewBox="0 0 600 290" aria-hidden="true"><path d="M120 136 C200 136 205 78 300 78" /><path className="async" d="M345 100 C370 150 415 160 480 160" /></svg>
            </div>
          </section>
        </div>
      </div>
    </section>

    <section className="landing-features" id="how-it-works">
      <article><span>01</span><h2>Scan locally</h2><p>Use only the language scanners you explicitly install. No account is required.</p></article>
      <article><span>02</span><h2>Explore both maps</h2><p>Inspect structural dependencies and inferred interactions between running deployables.</p></article>
      <article><span>03</span><h2>Verify the evidence</h2><p>Open the exact source and architecture evidence behind every relationship.</p></article>
    </section>
  </main>
}
