export const api = {
  async get(path, params = {}) {
    const query = new URLSearchParams(Object.entries(params).filter(([, v]) => v !== null && v !== undefined));
    const response = await fetch(`${path}?${query}`, { cache: 'no-store' });
    if (!response.ok) throw new Error(`${path}: ${response.status}`);
    return response.json();
  },
  async post(path, body) {
    try {
      const response = await fetch(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Milligram': '1' },
        body: JSON.stringify(body),
      });
      const result = await response.json().catch(() => null);
      if (!response.ok || !result) return { ok: false, message: result?.message || `Request failed (${response.status}).` };
      return result;
    } catch {
      return { ok: false, message: 'Cannot reach Milligram. Check that the viewer is running, then try again.' };
    }
  },
};
