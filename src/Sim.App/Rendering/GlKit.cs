using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Silk.NET.OpenGL;
using Sim.Core.Geometry;
using Mat3 = Sim.Core.Numerics.Mat3;
using Vec3 = Sim.Core.Numerics.Vec3;

namespace Sim.App.Rendering;

/// <summary>
/// GL 보조(셰이더 컴파일 · 투영 · 모델 행렬 · 다중 샘플 프레임버퍼 · PNG 스냅숏) — GlView 와 같은 규칙(NED → GL, 행벡터 관례)을
/// 다른 GL 컨트롤(풍동 보기)이 그대로 쓰도록 모은 것.
/// </summary>
internal static class GlKit
{
    public static string Header(bool es) => es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

    public static uint Program(GL gl, string vs, string fs)
    {
        uint Compile(ShaderType t, string src)
        {
            uint s = gl.CreateShader(t);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"셰이더 컴파일 실패: {gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, v); gl.AttachShader(p, f);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int lok);
        if (lok == 0) throw new InvalidOperationException($"셰이더 링크 실패: {gl.GetProgramInfoLog(p)}");
        gl.DeleteShader(v); gl.DeleteShader(f);
        return p;
    }

    public static Matrix4x4 Perspective(float fovy, float aspect, float n, float f)
    {
        float t = 1f / MathF.Tan(fovy / 2);
        return new Matrix4x4(t / aspect, 0, 0, 0, 0, t, 0, 0, 0, 0, (f + n) / (n - f), -1, 0, 0, 2 * f * n / (n - f), 0);
    }

    /// <summary>회전(물체 → NED) + 위치(카메라 기준 GL) → 모델 행렬(행벡터 관례).</summary>
    public static Matrix4x4 Model(Mat3 bodyToNed, Vec3 posGl)
    {
        var r = Frames.NedToGl * bodyToNed;
        return new Matrix4x4(
            (float)r.M11, (float)r.M21, (float)r.M31, 0,
            (float)r.M12, (float)r.M22, (float)r.M32, 0,
            (float)r.M13, (float)r.M23, (float)r.M33, 0,
            (float)posGl.X, (float)posGl.Y, (float)posGl.Z, 1);
    }

    /// <summary>현재 바인딩된 프레임버퍼(w×h)를 PNG 로 저장(위아래 뒤집기).</summary>
    public static unsafe void SaveSnapshot(GL gl, int w, int h, string path)
    {
        var buf = new byte[w * h * 4];
        fixed (byte* p = buf) gl.ReadPixels(0, 0, (uint)w, (uint)h, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, p);
        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
        using (var fb = bmp.Lock())
            for (int y = 0; y < h; y++)
                Marshal.Copy(buf, (h - 1 - y) * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bmp.Save(path);
    }
}

/// <summary>4× 다중 샘플 프레임버퍼(크기가 바뀌면 다시 만듦, 지원되지 않으면 Ensure 가 false — 그냥 그린다).</summary>
internal sealed class MsaaTarget
{
    private uint _fbo, _color, _depth;
    private int _w, _h;
    private bool _unsupported;

    public uint Fbo => _fbo;

    public bool Ensure(GL gl, int w, int h, int samples = 4)
    {
        if (_unsupported) return false;
        if (_fbo != 0 && _w == w && _h == h) return true;
        Delete(gl);
        gl.GetInteger(GLEnum.MaxSamples, out int max);
        uint n = (uint)Math.Min(samples, max);
        if (n < 2) { _unsupported = true; return false; }
        _color = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, n, InternalFormat.Rgba8, (uint)w, (uint)h);
        _depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, n, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _color);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        if (status != GLEnum.FramebufferComplete) { Delete(gl); _unsupported = true; return false; }
        _w = w; _h = h;
        return true;
    }

    /// <summary>다중 샘플 버퍼 → 대상 프레임버퍼 fb 로 내려 복사하고 fb 를 바인딩.</summary>
    public void Resolve(GL gl, uint fb)
    {
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fb);
        gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
    }

    public void Delete(GL gl)
    {
        if (_fbo != 0) gl.DeleteFramebuffer(_fbo);
        if (_color != 0) gl.DeleteRenderbuffer(_color);
        if (_depth != 0) gl.DeleteRenderbuffer(_depth);
        _fbo = _color = _depth = 0;
        _w = _h = 0;
    }
}
