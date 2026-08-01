export default function Loading() {
  return (
    <main className="shell" aria-busy="true">
      <header className="pageHeader">
        <h1>Relay</h1>
        <p className="lede" role="status">
          Loading…
        </p>
      </header>
      <div className="loadingPanel" aria-hidden="true">
        <span />
        <span />
        <span />
      </div>
    </main>
  );
}
