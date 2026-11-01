using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using Sim.Core.Analysis;

namespace Sim.Gpu;

/// <summary>
/// ILGPU 백엔드: "실행 1개 = GPU 스레드 1개". CUDA → OpenCL → (선택) ILGPU CPU 가속기 순으로 선택.
/// 커널은 Sim.Core.Analysis.DropModel.Simulate 를 그대로 호출한다(CPU 경로와 같은 코드).
/// </summary>
public sealed class GpuDropBackend : IDropBackend, IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accel;
    private readonly Action<Index1D, ArrayView<DropParams>, ArrayView<DropResult>> _kernel;

    public string Name { get; }
    public bool IsGpu { get; }

    private GpuDropBackend(Context ctx, Accelerator acc)
    {
        _context = ctx;
        _accel = acc;
        IsGpu = acc.AcceleratorType is AcceleratorType.Cuda or AcceleratorType.OpenCL;
        Name = $"ILGPU {acc.AcceleratorType}: {acc.Name}";
        _kernel = acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<DropParams>, ArrayView<DropResult>>(Kernel);
    }

    /// <summary>사용 가능한 가장 좋은 가속기. allowCpu=false 이고 GPU 가 없으면 null.</summary>
    public static GpuDropBackend? TryCreate(bool allowCpu, out string message)
    {
        try
        {
            var ctx = Context.Create(b => b.Default());
            Device? best = null;
            foreach (var t in new[] { AcceleratorType.Cuda, AcceleratorType.OpenCL })
            {
                best = ctx.Devices.Where(d => d.AcceleratorType == t).OrderByDescending(d => d.MemorySize).FirstOrDefault();
                if (best != null) break;
            }
            if (best == null && allowCpu) best = ctx.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            if (best == null)
            {
                ctx.Dispose();
                message = "GPU(CUDA/OpenCL) 장치를 찾지 못했습니다.";
                return null;
            }
            var acc = best.CreateAccelerator(ctx);
            var be = new GpuDropBackend(ctx, acc);
            message = be.Name;
            return be;
        }
        catch (Exception ex)
        {
            message = $"ILGPU 초기화 실패: {ex.Message}";
            return null;
        }
    }

    public static IReadOnlyList<string> ListDevices()
    {
        using var ctx = Context.Create(b => b.Default());
        return ctx.Devices.Select(d => $"{d.AcceleratorType}: {d.Name} ({d.MemorySize / (1024 * 1024)} MB)").ToList();
    }

    private static void Kernel(Index1D i, ArrayView<DropParams> ps, ArrayView<DropResult> rs)
    {
        rs[i] = DropModel.Simulate(ps[i]);
    }

    public DropResult[] Run(DropParams[] ps)
    {
        if (ps.Length == 0) return [];
        using var pBuf = _accel.Allocate1D(ps);
        using var rBuf = _accel.Allocate1D<DropResult>(ps.Length);
        _kernel((int)pBuf.Length, pBuf.View, rBuf.View);
        _accel.Synchronize();
        return rBuf.GetAsArray1D();
    }

    public void Dispose()
    {
        _accel.Dispose();
        _context.Dispose();
    }
}
