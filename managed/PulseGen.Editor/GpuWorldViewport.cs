using Avalonia;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using PulseGen.Contracts;
using System.Diagnostics;

namespace PulseGen.Editor;

internal readonly record struct ViewportPerformanceStatus(
    bool Ready,
    string Backend,
    int TriangleCount,
    int TerrainTriangleCount,
    int ImportedTriangleCount,
    int ImportedMeshCount,
    long FrameCount,
    double ElapsedSeconds,
    double FramesPerSecond,
    double CpuSubmitMillisecondsAverage,
    double CpuSubmitMillisecondsMaximum,
    double FrameIntervalMillisecondsMaximum,
    long SlowFrameCount,
    string? Error);

public sealed unsafe class GpuWorldViewport : OpenGlControlBase
{
    private const int GlArrayBuffer = 0x8892;
    private const int GlElementArrayBuffer = 0x8893;
    private const int GlStaticDraw = 0x88E4;
    private const int GlFloat = 0x1406;
    private const int GlUnsignedInt = 0x1405;
    private const int GlTriangles = 0x0004;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlDepthBufferBit = 0x00000100;
    private const int GlDepthTest = 0x0B71;
    private const int GlLequal = 0x0203;
    private const int GlFramebuffer = 0x8D40;
    private const int GlVertexShader = 0x8B31;
    private const int GlFragmentShader = 0x8B30;
    private const double SlowFrameThresholdMilliseconds = 1000.0 / 55.0;
    private const double IdleMeasurementResetMilliseconds = 250;

    private SceneDocument? scene;
    private string? projectDirectory;
    private ViewportMeshBuildKey queuedMeshKey;
    private bool hasQueuedMeshKey;
    private CancellationTokenSource? meshBuildCancellation;
    private ViewportMesh? pendingMesh;
    private ViewportMesh activeMesh = ViewportMesh.Empty;
    private int vertexBuffer;
    private int indexBuffer;
    private int vertexArray;
    private int shaderProgram;
    private int matrixUniform = -1;
    private bool continuousRendering;
    private bool initialized;
    private string backend = "OpenGL starting";
    private string? error;
    private long frameCount;
    private long measurementStartedTimestamp;
    private long previousFrameTimestamp;
    private long cpuSubmitTicksTotal;
    private long cpuSubmitTicksMaximum;
    private long frameIntervalTicksMaximum;
    private long slowFrameCount;
    private long nextPerformancePublishTimestamp;
    private ViewportCameraController camera = new();

    public GpuWorldViewport()
    {
        camera.Changed += OnCameraChanged;
    }

    public SceneDocument? Scene
    {
        get => scene;
        set => SetScene(value, projectDirectory);
    }

    public string? ProjectDirectory
    {
        get => projectDirectory;
        set => SetScene(scene, value);
    }

    internal ViewportCameraController Camera
    {
        get => camera;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(camera, value))
            {
                return;
            }
            camera.Changed -= OnCameraChanged;
            camera = value;
            camera.Changed += OnCameraChanged;
            RequestNextFrameRendering();
        }
    }

    public void SetScene(SceneDocument? value, string? owningProjectDirectory)
    {
        var normalized = owningProjectDirectory is null ? null : Path.GetFullPath(owningProjectDirectory);
        scene = value;
        projectDirectory = normalized;
        QueueMeshBuild(value);
    }

    public bool ContinuousRendering
    {
        get => continuousRendering;
        set
        {
            if (continuousRendering == value)
            {
                return;
            }
            continuousRendering = value;
            if (value)
            {
                RequestNextFrameRendering();
            }
        }
    }

    internal ViewportPerformanceStatus PerformanceStatus => CreatePerformanceStatus();

    internal event Action<ViewportPerformanceStatus>? PerformanceUpdated;

    internal void LoadMesh(ViewportMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TriangleCount > 0)
        {
            camera.ExtendContentBounds(
                mesh.BoundsMinimum,
                mesh.BoundsMaximum,
                fit: activeMesh.TriangleCount == 0);
        }
        Interlocked.Exchange(ref pendingMesh, mesh);
        error = null;
        ResetPerformanceMeasurement();
        RequestNextFrameRendering();
    }

    internal ViewportPickResult? Pick(Point point) =>
        activeMesh.TriangleCount == 0
            ? null
            : ViewportPicker.Pick(activeMesh, camera.Ray(point, Bounds.Size));

    internal void FitCameraToScene() => camera.Fit();

    internal void ResetPerformanceMeasurement()
    {
        frameCount = 0;
        measurementStartedTimestamp = 0;
        previousFrameTimestamp = 0;
        cpuSubmitTicksTotal = 0;
        cpuSubmitTicksMaximum = 0;
        frameIntervalTicksMaximum = 0;
        slowFrameCount = 0;
        nextPerformancePublishTimestamp = 0;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            backend = $"{gl.Renderer} · {gl.Version}";
            shaderProgram = BuildShaderProgram(gl);
            matrixUniform = gl.GetUniformLocationString(shaderProgram, "uViewProjection");
            vertexBuffer = gl.GenBuffer();
            indexBuffer = gl.GenBuffer();
            if (gl.IsGenVertexArraysAvailable)
            {
                vertexArray = gl.GenVertexArray();
            }
            initialized = true;
            error = null;
            RequestNextFrameRendering();
        }
        catch (Exception exception)
        {
            initialized = false;
            error = exception.Message;
            PublishPerformance(force: true);
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        var submitStarted = Stopwatch.GetTimestamp();
        var frameTimestamp = submitStarted;
        try
        {
            gl.BindFramebuffer(GlFramebuffer, framebuffer);
            gl.Viewport(0, 0, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
            gl.ClearColor(0.035F, 0.051F, 0.071F, 1F);
            gl.ClearDepth(1F);
            gl.Clear(GlColorBufferBit | GlDepthBufferBit);
            if (initialized)
            {
                UploadPendingMesh(gl);
            }

            if (initialized && activeMesh.TriangleCount > 0 && shaderProgram != 0)
            {
                gl.Enable(GlDepthTest);
                gl.DepthFunc(GlLequal);
                gl.UseProgram(shaderProgram);
                if (vertexArray != 0)
                {
                    gl.BindVertexArray(vertexArray);
                }
                gl.BindBuffer(GlArrayBuffer, vertexBuffer);
                gl.BindBuffer(GlElementArrayBuffer, indexBuffer);
                gl.EnableVertexAttribArray(0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(0, 3, GlFloat, 0, ViewportMesh.FloatsPerVertex * sizeof(float), IntPtr.Zero);
                gl.VertexAttribPointer(1, 3, GlFloat, 0, ViewportMesh.FloatsPerVertex * sizeof(float), new IntPtr(3 * sizeof(float)));
                var viewProjection = camera.ViewProjection(Bounds.Size);
                gl.UniformMatrix4fv(matrixUniform, 1, false, &viewProjection.M11);
                gl.DrawElements(GlTriangles, activeMesh.Indices.Length, GlUnsignedInt, IntPtr.Zero);
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            initialized = false;
        }
        finally
        {
            RecordFrame(frameTimestamp, Stopwatch.GetTimestamp() - submitStarted);
            PublishPerformance(force: !initialized);
            if (continuousRendering && initialized)
            {
                RequestNextFrameRendering();
            }
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (activeMesh.TriangleCount > 0)
        {
            Interlocked.CompareExchange(ref pendingMesh, activeMesh, null);
        }
        ReleaseGlResources(gl);
        initialized = false;
    }

    protected override void OnOpenGlLost()
    {
        initialized = false;
        error = "The OpenGL context was lost; Avalonia will recreate the viewport when the surface becomes available.";
        PublishPerformance(force: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        meshBuildCancellation?.Cancel();
        meshBuildCancellation?.Dispose();
        meshBuildCancellation = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void QueueMeshBuild(SceneDocument? value)
    {
        var nextKey = value is null ? default : ViewportMeshBuilder.KeyFor(value, projectDirectory);
        if (hasQueuedMeshKey && nextKey == queuedMeshKey)
        {
            return;
        }
        queuedMeshKey = nextKey;
        hasQueuedMeshKey = true;
        meshBuildCancellation?.Cancel();
        meshBuildCancellation?.Dispose();
        meshBuildCancellation = null;
        if (value is null)
        {
            LoadMesh(ViewportMesh.Empty);
            return;
        }

        var cancellation = new CancellationTokenSource();
        meshBuildCancellation = cancellation;
        var buildProjectDirectory = projectDirectory;
        _ = Task.Run(
                () => ViewportMeshBuilder.Build(value, buildProjectDirectory, cancellation.Token),
                cancellation.Token)
            .ContinueWith(
                task =>
                {
                    Dispatcher.UIThread.Post(() => CompleteMeshBuild(task, cancellation));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void CompleteMeshBuild(Task<ViewportMesh> task, CancellationTokenSource cancellation)
    {
        if (meshBuildCancellation != cancellation)
        {
            return;
        }
        if (task.Status == TaskStatus.RanToCompletion)
        {
            LoadMesh(task.Result);
        }
        else if (task.IsFaulted)
        {
            error = task.Exception?.GetBaseException().Message ?? "Viewport mesh preparation failed.";
            PublishPerformance(force: true);
        }
        meshBuildCancellation.Dispose();
        meshBuildCancellation = null;
    }

    private void UploadPendingMesh(GlInterface gl)
    {
        var mesh = Interlocked.Exchange(ref pendingMesh, null);
        if (mesh is null)
        {
            return;
        }
        activeMesh = mesh;
        if (mesh.TriangleCount == 0)
        {
            return;
        }

        if (vertexArray != 0)
        {
            gl.BindVertexArray(vertexArray);
        }
        fixed (float* vertices = mesh.InterleavedPositionNormals)
        fixed (uint* indices = mesh.Indices)
        {
            gl.BindBuffer(GlArrayBuffer, vertexBuffer);
            gl.BufferData(
                GlArrayBuffer,
                new IntPtr(checked(mesh.InterleavedPositionNormals.Length * sizeof(float))),
                new IntPtr(vertices),
                GlStaticDraw);
            gl.BindBuffer(GlElementArrayBuffer, indexBuffer);
            gl.BufferData(
                GlElementArrayBuffer,
                new IntPtr(checked(mesh.Indices.Length * sizeof(uint))),
                new IntPtr(indices),
                GlStaticDraw);
        }
    }

    private int BuildShaderProgram(GlInterface gl)
    {
        var modern = GlVersion.Major >= 3 && !GlVersion.IsCompatibilityProfile;
        var embedded = GlVersion.Type == GlProfileType.OpenGLES;
        var vertexSource = modern
            ? embedded
                ? "#version 300 es\nprecision highp float;\nin vec3 aPosition;\nin vec3 aNormal;\nuniform mat4 uViewProjection;\nout float vLight;\nvoid main(){ gl_Position = uViewProjection * vec4(aPosition, 1.0); vLight = 0.32 + 0.68 * max(dot(normalize(aNormal), normalize(vec3(0.35, 1.0, 0.45))), 0.0); }"
                : "#version 330 core\nin vec3 aPosition;\nin vec3 aNormal;\nuniform mat4 uViewProjection;\nout float vLight;\nvoid main(){ gl_Position = uViewProjection * vec4(aPosition, 1.0); vLight = 0.32 + 0.68 * max(dot(normalize(aNormal), normalize(vec3(0.35, 1.0, 0.45))), 0.0); }"
            : embedded
                ? "#version 100\nprecision highp float;\nattribute vec3 aPosition;\nattribute vec3 aNormal;\nuniform mat4 uViewProjection;\nvarying float vLight;\nvoid main(){ gl_Position = uViewProjection * vec4(aPosition, 1.0); vLight = 0.32 + 0.68 * max(dot(normalize(aNormal), normalize(vec3(0.35, 1.0, 0.45))), 0.0); }"
                : "#version 120\nattribute vec3 aPosition;\nattribute vec3 aNormal;\nuniform mat4 uViewProjection;\nvarying float vLight;\nvoid main(){ gl_Position = uViewProjection * vec4(aPosition, 1.0); vLight = 0.32 + 0.68 * max(dot(normalize(aNormal), normalize(vec3(0.35, 1.0, 0.45))), 0.0); }";
        var fragmentSource = modern
            ? embedded
                ? "#version 300 es\nprecision highp float;\nin float vLight;\nout vec4 outputColor;\nvoid main(){ outputColor = vec4(vec3(0.10, 0.43, 0.48) * vLight, 1.0); }"
                : "#version 330 core\nin float vLight;\nout vec4 outputColor;\nvoid main(){ outputColor = vec4(vec3(0.10, 0.43, 0.48) * vLight, 1.0); }"
            : embedded
                ? "#version 100\nprecision highp float;\nvarying float vLight;\nvoid main(){ gl_FragColor = vec4(vec3(0.10, 0.43, 0.48) * vLight, 1.0); }"
                : "#version 120\nvarying float vLight;\nvoid main(){ gl_FragColor = vec4(vec3(0.10, 0.43, 0.48) * vLight, 1.0); }";
        var vertexShader = 0;
        var fragmentShader = 0;
        var program = 0;
        try
        {
            vertexShader = CompileShader(gl, GlVertexShader, vertexSource, "vertex");
            fragmentShader = CompileShader(gl, GlFragmentShader, fragmentSource, "fragment");
            program = gl.CreateProgram();
            gl.AttachShader(program, vertexShader);
            gl.AttachShader(program, fragmentShader);
            gl.BindAttribLocationString(program, 0, "aPosition");
            gl.BindAttribLocationString(program, 1, "aNormal");
            var linkError = gl.LinkProgramAndGetError(program);
            if (!string.IsNullOrWhiteSpace(linkError))
            {
                throw new InvalidOperationException($"Viewport shader link failed: {linkError}");
            }
            return program;
        }
        catch
        {
            if (program != 0)
            {
                gl.DeleteProgram(program);
            }
            throw;
        }
        finally
        {
            if (vertexShader != 0)
            {
                gl.DeleteShader(vertexShader);
            }
            if (fragmentShader != 0)
            {
                gl.DeleteShader(fragmentShader);
            }
        }
    }

    private static int CompileShader(GlInterface gl, int type, string source, string stage)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSourceString(shader, source);
        var compileError = gl.CompileShaderAndGetError(shader, source);
        if (string.IsNullOrWhiteSpace(compileError))
        {
            return shader;
        }
        gl.DeleteShader(shader);
        throw new InvalidOperationException($"Viewport {stage} shader compilation failed: {compileError}");
    }

    private void RecordFrame(long timestamp, long cpuSubmitTicks)
    {
        if (measurementStartedTimestamp == 0)
        {
            measurementStartedTimestamp = timestamp;
        }
        if (previousFrameTimestamp != 0)
        {
            var interval = timestamp - previousFrameTimestamp;
            var intervalMilliseconds = interval * 1000.0 / Stopwatch.Frequency;
            if (!continuousRendering && intervalMilliseconds > IdleMeasurementResetMilliseconds)
            {
                measurementStartedTimestamp = timestamp;
                frameCount = 0;
                cpuSubmitTicksTotal = 0;
                cpuSubmitTicksMaximum = 0;
                frameIntervalTicksMaximum = 0;
                slowFrameCount = 0;
            }
            else
            {
                frameIntervalTicksMaximum = Math.Max(frameIntervalTicksMaximum, interval);
                if (intervalMilliseconds > SlowFrameThresholdMilliseconds)
                {
                    slowFrameCount++;
                }
            }
        }
        previousFrameTimestamp = timestamp;
        frameCount++;
        cpuSubmitTicksTotal += cpuSubmitTicks;
        cpuSubmitTicksMaximum = Math.Max(cpuSubmitTicksMaximum, cpuSubmitTicks);
    }

    private ViewportPerformanceStatus CreatePerformanceStatus()
    {
        var elapsed = measurementStartedTimestamp == 0
            ? 0
            : (Stopwatch.GetTimestamp() - measurementStartedTimestamp) / (double)Stopwatch.Frequency;
        var fps = elapsed > 0 && frameCount > 1 ? (frameCount - 1) / elapsed : 0;
        return new(
            initialized,
            backend,
            activeMesh.TriangleCount,
            activeMesh.TerrainTriangleCount,
            activeMesh.ImportedTriangleCount,
            activeMesh.ImportedMeshCount,
            frameCount,
            elapsed,
            fps,
            frameCount == 0 ? 0 : cpuSubmitTicksTotal * 1000.0 / Stopwatch.Frequency / frameCount,
            cpuSubmitTicksMaximum * 1000.0 / Stopwatch.Frequency,
            frameIntervalTicksMaximum * 1000.0 / Stopwatch.Frequency,
            slowFrameCount,
            error);
    }

    private void PublishPerformance(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now < nextPerformancePublishTimestamp)
        {
            return;
        }
        nextPerformancePublishTimestamp = now + (Stopwatch.Frequency / 4);
        PerformanceUpdated?.Invoke(CreatePerformanceStatus());
    }

    private void OnCameraChanged(object? sender, EventArgs e) => RequestNextFrameRendering();

    private void ReleaseGlResources(GlInterface gl)
    {
        if (vertexBuffer != 0)
        {
            gl.DeleteBuffer(vertexBuffer);
            vertexBuffer = 0;
        }
        if (indexBuffer != 0)
        {
            gl.DeleteBuffer(indexBuffer);
            indexBuffer = 0;
        }
        if (vertexArray != 0 && gl.IsDeleteVertexArraysAvailable)
        {
            gl.DeleteVertexArray(vertexArray);
            vertexArray = 0;
        }
        if (shaderProgram != 0)
        {
            gl.DeleteProgram(shaderProgram);
            shaderProgram = 0;
        }
    }
}
