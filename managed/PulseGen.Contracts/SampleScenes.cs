namespace PulseGen.Contracts;

public static class SampleScenes
{
    public static SceneDocument CreateFlyby()
    {
        var pathId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
        var emitterId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");
        var oscillatorId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003");
        var outputId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000004");
        var stoneId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000005");
        var roomId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000006");
        var headId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000007");
        var microphonesId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000008");
        var profileId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000009");
        var windId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000010");

        return new SceneDocument
        {
            ProjectId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000000"),
            Name = "Physics Flyby",
            Atmosphere = new AtmosphereField
            {
                TemperatureCelsius = 20,
                RelativeHumidityPercent = 50,
                PressureKilopascals = 101.325,
                WindZones =
                [
                    new WindZone
                    {
                        Id = windId,
                        Name = "Crosswind",
                        DimensionsMetres = new(80, 20, 40),
                        VelocityMetresPerSecond = new(0, 0, 2),
                        Turbulence = 0.08,
                        NoiseSeed = 42
                    }
                ]
            },
            MotionPaths =
            [
                new MotionPath
                {
                    Id = pathId,
                    Name = "Fast left-to-right flyby",
                    Kind = MotionPathKind.CatmullRom,
                    Keyframes =
                    [
                        new MotionKeyframe { TimeSeconds = 0, PositionMetres = new(-28, 1.7, 4) },
                        new MotionKeyframe { TimeSeconds = 2.5, PositionMetres = new(-4, 1.7, 1.5) },
                        new MotionKeyframe { TimeSeconds = 3.5, PositionMetres = new(4, 1.7, 1.5) },
                        new MotionKeyframe { TimeSeconds = 6, PositionMetres = new(28, 1.7, 4) }
                    ]
                }
            ],
            Emitters =
            [
                new Emitter
                {
                    Id = emitterId,
                    Name = "440 Hz moving primitive",
                    MotionPathId = pathId,
                    GainLinear = 0.8,
                    MinDistanceMetres = 1,
                    SignalGraph = new SignalGraph
                    {
                        Nodes =
                        [
                            new SignalNode
                            {
                                Id = oscillatorId,
                                Name = "Sine",
                                Kind = SignalNodeKind.Oscillator,
                                Waveform = WaveformKind.Sine,
                                NumericParameters = new Dictionary<string, double> { ["frequencyHz"] = 440, ["amplitude"] = 1 }
                            },
                            new SignalNode { Id = outputId, Name = "Mono emitter output", Kind = SignalNodeKind.Output }
                        ],
                        Connections =
                        [
                            new SignalConnection { FromNodeId = oscillatorId, FromPort = "out", ToNodeId = outputId, ToPort = "in" }
                        ]
                    }
                }
            ],
            Materials =
            [
                new AcousticMaterial
                {
                    Id = stoneId,
                    Name = "Dense stone",
                    Absorption = [0.02, 0.02, 0.025, 0.03, 0.04, 0.05, 0.07, 0.1],
                    Scattering = [0.08, 0.08, 0.1, 0.12, 0.16, 0.2, 0.28, 0.35],
                    Transmission = [0.01, 0.008, 0.005, 0.003, 0.002, 0.001, 0, 0],
                    Roughness = 0.4
                }
            ],
            Geometry =
            [
                new AcousticGeometry
                {
                    Id = roomId,
                    Name = "Stone simulation hall",
                    Kind = GeometryKind.ParametricRoom,
                    MaterialId = stoneId,
                    Transform = new Transform { PositionMetres = new(0, 4, 0) },
                    DimensionsMetres = new(64, 8, 18)
                }
            ],
            ReceiverRigs =
            [
                new ReceiverRig
                {
                    Id = headId,
                    Name = "Parametric listener",
                    Kind = ReceiverRigKind.BinauralHead,
                    Transform = new Transform { PositionMetres = new(0, 1.7, 0) },
                    SpacingMetres = 0.18,
                    HeadRadiusMetres = 0.09,
                    HrtfMode = HrtfMode.Parametric
                },
                new ReceiverRig
                {
                    Id = microphonesId,
                    Name = "ORTF-style microphones",
                    Kind = ReceiverRigKind.StereoMicrophone,
                    Transform = new Transform { PositionMetres = new(0, 1.7, -1) },
                    SpacingMetres = 0.17,
                    AngleDegrees = 110,
                    PolarPattern = PolarPattern.Cardioid,
                    SensitivityDb = -6
                }
            ],
            Timeline = new EventTimeline
            {
                DurationSeconds = 6,
                Events =
                [
                    new TimelineEvent { Id = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000011"), Kind = TimelineEventKind.StartEmitter, TimeSeconds = 0, TargetId = emitterId },
                    new TimelineEvent { Id = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000012"), Kind = TimelineEventKind.EndCapture, TimeSeconds = 6, Label = "End flyby" }
                ]
            },
            RenderProfiles =
            [
                new RenderProfile
                {
                    Id = profileId,
                    Name = "Interactive 48k",
                    Quality = RenderQuality.Interactive,
                    SampleRate = 48000,
                    BufferFrames = 512,
                    ReflectionOrder = 1,
                    RayCount = 1024,
                    ImpulseResponseSeconds = 2,
                    Seed = 42
                }
            ]
        };
    }
}

