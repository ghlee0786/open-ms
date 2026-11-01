namespace Sim.App.Rendering;

/// <summary>
/// 3D 뷰 GPU 버퍼 장부(GL 무관 — 할당·해제는 호출자가 넘기는 함수, 테스트 프로젝트가 이 소스를 링크한다).
/// · 메시 버퍼(위치·법선) = 물체마다 1개 — 분할 화면 수와 무관하게 모든 화면이 공유한다.
/// · 면 색 버퍼(해석 조건) = (분할 화면, 물체)마다 1개 — 화면마다 다른 것은 이 버퍼와 카메라뿐.
/// 색 버퍼는 공유 메시 버퍼를 참조해 만든다(같은 VBO 를 묶은 VAO + 색 VBO).
/// </summary>
public sealed class SurfaceBufferCache<T> where T : struct
{
    private readonly Func<int, T> _createMesh;
    private readonly Func<int, int, T, T> _createColor;
    private readonly Action<T> _delete;
    private readonly Dictionary<int, T> _mesh = new();
    private readonly Dictionary<(int Pane, int Body), (T Buf, int Version)> _color = new();

    /// <param name="createMesh">물체 → 메시 버퍼.</param>
    /// <param name="createColor">(분할 화면, 물체, 그 물체의 공유 메시 버퍼) → 색 버퍼.</param>
    /// <param name="delete">버퍼 해제.</param>
    public SurfaceBufferCache(Func<int, T> createMesh, Func<int, int, T, T> createColor, Action<T> delete)
    {
        _createMesh = createMesh;
        _createColor = createColor;
        _delete = delete;
    }

    public int MeshBufferCount => _mesh.Count;
    public int ColorBufferCount => _color.Count;
    /// <summary>지금까지 만든 메시 버퍼 수(해제 포함 누계) — 분할을 바꿔도 늘지 않아야 한다.</summary>
    public long MeshCreations { get; private set; }
    public long ColorCreations { get; private set; }
    public IEnumerable<T> MeshBuffers => _mesh.Values;
    public IEnumerable<T> ColorBuffers => _color.Values.Select(c => c.Buf);

    /// <summary>물체의 공유 메시 버퍼(없으면 만든다).</summary>
    public T Mesh(int body)
    {
        if (_mesh.TryGetValue(body, out var m)) return m;
        m = _createMesh(body);
        MeshCreations++;
        _mesh[body] = m;
        return m;
    }

    public bool TryGetMesh(int body, out T mesh) => _mesh.TryGetValue(body, out mesh);

    /// <summary>
    /// 분할 화면 pane 의 물체 색 버퍼. Stale = 버퍼에 담긴 색 버전이 version 과 다름(호출자가 색을 다시 올리고 MarkUploaded).
    /// </summary>
    public (T Buf, bool Stale) Color(int pane, int body, int version)
    {
        var key = (pane, body);
        if (!_color.TryGetValue(key, out var c))
        {
            c = (_createColor(pane, body, Mesh(body)), int.MinValue);
            ColorCreations++;
            _color[key] = c;
        }
        return (c.Buf, c.Version != version);
    }

    public void MarkUploaded(int pane, int body, int version)
    {
        if (_color.TryGetValue((pane, body), out var c)) _color[(pane, body)] = (c.Buf, version);
    }

    /// <summary>pane 이상 번호 분할 화면의 색 버퍼 해제(분할 수를 줄였을 때).</summary>
    public void TrimPanes(int paneCount)
    {
        foreach (var key in _color.Keys.Where(k => k.Pane >= paneCount).ToList())
        {
            _delete(_color[key].Buf);
            _color.Remove(key);
        }
    }

    /// <summary>모두 해제(장면 교체 · GL 종료).</summary>
    public void Clear()
    {
        foreach (var c in _color.Values) _delete(c.Buf);
        foreach (var m in _mesh.Values) _delete(m);
        _color.Clear();
        _mesh.Clear();
    }
}
