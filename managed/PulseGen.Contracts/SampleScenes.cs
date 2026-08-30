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
            CreatedAtUtc = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
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

    public static SceneDocument CreateSurfaceEcho()
    {
        var scene = CreateFlyby();
        var sourcePosition = new Vector3d(-3, 1.7, 0);
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Name = "Stationary impulse source",
            Kind = MotionPathKind.Linear,
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = sourcePosition },
                new MotionKeyframe { TimeSeconds = 3, PositionMetres = sourcePosition }
            ]
        };
        var sourceNodeIndex = scene.Emitters[0].SignalGraph.Nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
        scene.Emitters[0].SignalGraph.Nodes[sourceNodeIndex] = scene.Emitters[0].SignalGraph.Nodes[sourceNodeIndex] with
        {
            Name = "One hertz impulse train",
            Waveform = WaveformKind.Impulse,
            NumericParameters = new Dictionary<string, double> { ["frequencyHz"] = 1, ["amplitude"] = 1 }
        };
        scene.Emitters[0] = scene.Emitters[0] with { Name = "Impulse emitter", GainLinear = 0.9, MinDistanceMetres = 0.25 };

        var stoneId = Guid.Parse("cccccccc-0000-4000-8000-000000000001");
        var curtainId = Guid.Parse("cccccccc-0000-4000-8000-000000000002");
        var timberId = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
        scene.Materials.Clear();
        scene.Materials.AddRange(
        [
            new AcousticMaterial
            {
                Id = stoneId,
                Name = "Polished stone",
                Absorption = [0.01, 0.015, 0.02, 0.025, 0.035, 0.05, 0.08, 0.12],
                Scattering = [0.03, 0.03, 0.04, 0.05, 0.08, 0.12, 0.18, 0.24],
                Transmission = [0.01, 0.008, 0.005, 0.003, 0.002, 0.001, 0, 0],
                Roughness = 0.18
            },
            new AcousticMaterial
            {
                Id = curtainId,
                Name = "Heavy acoustic curtain",
                Absorption = [0.08, 0.18, 0.42, 0.68, 0.82, 0.9, 0.94, 0.96],
                Scattering = [0.12, 0.14, 0.18, 0.22, 0.28, 0.34, 0.4, 0.45],
                Transmission = [0.12, 0.08, 0.04, 0.02, 0.01, 0.005, 0.002, 0.001],
                Roughness = 0.85
            },
            new AcousticMaterial
            {
                Id = timberId,
                Name = "Dense timber baffle",
                Absorption = [0.12, 0.1, 0.09, 0.08, 0.08, 0.1, 0.14, 0.2],
                Scattering = [0.18, 0.18, 0.2, 0.24, 0.3, 0.36, 0.42, 0.48],
                Transmission = [0.18, 0.14, 0.1, 0.08, 0.05, 0.03, 0.015, 0.008],
                Roughness = 0.52
            }
        ]);

        scene.Geometry.Clear();
        scene.Geometry.AddRange(
        [
            new AcousticGeometry
            {
                Id = Guid.Parse("cccccccc-0000-4000-8000-000000000004"),
                Name = "Stone reflector",
                Kind = GeometryKind.Plane,
                MaterialId = stoneId,
                Transform = new Transform { PositionMetres = new(0, 1.7, 4) },
                DimensionsMetres = new(12, 5, 0),
                Occludes = true,
                Reflects = true
            },
            new AcousticGeometry
            {
                Id = Guid.Parse("cccccccc-0000-4000-8000-000000000005"),
                Name = "Curtain reflector",
                Kind = GeometryKind.Plane,
                MaterialId = curtainId,
                Transform = new Transform { PositionMetres = new(0, 1.7, -4), RotationDegrees = new(0, 180, 0) },
                DimensionsMetres = new(12, 5, 0),
                Occludes = true,
                Reflects = true
            },
            new AcousticGeometry
            {
                Id = Guid.Parse("cccccccc-0000-4000-8000-000000000006"),
                Name = "Timber transmission baffle",
                Kind = GeometryKind.Box,
                MaterialId = timberId,
                Transform = new Transform { PositionMetres = new(-1.5, 1.7, 0), RotationDegrees = new(0, 12, 0) },
                DimensionsMetres = new(0.24, 3.2, 2.4),
                Occludes = true,
                Reflects = true
            }
        ]);

        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with
        {
            Name = "Surface comparison listener",
            Transform = new Transform { PositionMetres = new(0, 1.7, 0) }
        };
        scene.ReceiverRigs[1] = scene.ReceiverRigs[1] with
        {
            Name = "Surface comparison microphones",
            Transform = new Transform { PositionMetres = new(0, 1.7, 0) },
            PolarPattern = PolarPattern.Omni,
            SensitivityDb = 0
        };
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with
        {
            Name = "Final surface comparison 48k",
            Quality = RenderQuality.Final,
            ReflectionOrder = 1,
            RayCount = 4096,
            Seed = 314159
        };
        return scene with
        {
            ProjectId = Guid.Parse("cccccccc-0000-4000-8000-000000000000"),
            Name = "Surface Echo Study",
            Atmosphere = new AtmosphereField { TemperatureCelsius = 20, RelativeHumidityPercent = 45, PressureKilopascals = 101.325 },
            Timeline = new EventTimeline
            {
                DurationSeconds = 3,
                Events =
                [
                    new TimelineEvent { Id = Guid.Parse("cccccccc-0000-4000-8000-000000000007"), Kind = TimelineEventKind.StartEmitter, TimeSeconds = 0, TargetId = scene.Emitters[0].Id },
                    new TimelineEvent { Id = Guid.Parse("cccccccc-0000-4000-8000-000000000008"), Kind = TimelineEventKind.EndCapture, TimeSeconds = 3, Label = "End material comparison" }
                ]
            },
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)
        };
    }

    public static SceneDocument CreateMovingReceiver()
    {
        var scene = CreateFlyby();
        var sourcePosition = new Vector3d(0, 1.7, 40);
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Name = "Stationary Doppler reference source",
            Kind = MotionPathKind.Linear,
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = sourcePosition },
                new MotionKeyframe { TimeSeconds = 2, PositionMetres = sourcePosition }
            ]
        };
        scene.Emitters[0] = scene.Emitters[0] with
        {
            Name = "Stationary 440 Hz reference",
            GainLinear = 1,
            MinDistanceMetres = 0.25
        };
        scene.Geometry.Clear();
        var receiverPathId = Guid.Parse("dddddddd-0000-4000-8000-000000000001");
        scene.MotionPaths.Add(new MotionPath
        {
            Id = receiverPathId,
            Name = "Microphone approach at 10 m/s",
            Kind = MotionPathKind.Linear,
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = new(0, 1.7, 0) },
                new MotionKeyframe { TimeSeconds = 2, PositionMetres = new(0, 1.7, 20) }
            ]
        });
        scene.ReceiverRigs[0] = scene.ReceiverRigs[0] with
        {
            Name = "Static listener reference",
            Transform = new Transform { PositionMetres = new(0, 1.7, 0) }
        };
        scene.ReceiverRigs[1] = scene.ReceiverRigs[1] with
        {
            Name = "Moving microphone pair",
            Transform = new Transform { PositionMetres = new(0, 1.7, 0) },
            MotionPathId = receiverPathId,
            PolarPattern = PolarPattern.Omni,
            SensitivityDb = 0
        };
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with
        {
            Name = "Moving receiver final 48k",
            Quality = RenderQuality.Final,
            ReflectionOrder = 0,
            RayCount = 0,
            ImpulseResponseSeconds = 0,
            Seed = 271828
        };
        return scene with
        {
            ProjectId = Guid.Parse("dddddddd-0000-4000-8000-000000000000"),
            Name = "Moving Receiver Doppler",
            Atmosphere = new AtmosphereField
            {
                TemperatureCelsius = 20,
                RelativeHumidityPercent = 50,
                PressureKilopascals = 101.325
            },
            Timeline = new EventTimeline
            {
                DurationSeconds = 2,
                Events =
                [
                    new TimelineEvent { Id = Guid.Parse("dddddddd-0000-4000-8000-000000000002"), Kind = TimelineEventKind.StartEmitter, TimeSeconds = 0, TargetId = scene.Emitters[0].Id },
                    new TimelineEvent { Id = Guid.Parse("dddddddd-0000-4000-8000-000000000003"), Kind = TimelineEventKind.EndCapture, TimeSeconds = 2, Label = "End receiver fly-in" }
                ]
            },
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)
        };
    }

    public static SceneDocument CreateAtmosphereField()
    {
        var scene = CreateFlyby();
        var sourcePosition = new Vector3d(-30, 7, 0);
        scene.MotionPaths[0] = scene.MotionPaths[0] with
        {
            Name = "Stationary atmospheric impulse source",
            Kind = MotionPathKind.Linear,
            Keyframes =
            [
                new MotionKeyframe { TimeSeconds = 0, PositionMetres = sourcePosition },
                new MotionKeyframe { TimeSeconds = 2, PositionMetres = sourcePosition }
            ]
        };
        var sourceNodeIndex = scene.Emitters[0].SignalGraph.Nodes.FindIndex(node => node.Kind == SignalNodeKind.Oscillator);
        scene.Emitters[0].SignalGraph.Nodes[sourceNodeIndex] = scene.Emitters[0].SignalGraph.Nodes[sourceNodeIndex] with
        {
            Name = "One hertz atmospheric impulse",
            Waveform = WaveformKind.Impulse,
            NumericParameters = new Dictionary<string, double>
            {
                ["frequencyHz"] = 1,
                ["amplitude"] = 1,
                ["seed"] = 161803
            }
        };
        scene.Emitters[0] = scene.Emitters[0] with
        {
            Name = "Atmospheric timing impulse",
            GainLinear = 1,
            MinDistanceMetres = 0.25
        };
        scene.Geometry.Clear();
        scene.ReceiverRigs.Clear();
        scene.ReceiverRigs.AddRange(
        [
            new ReceiverRig
            {
                Id = Guid.Parse("eeeeeeee-0000-4000-8000-000000000001"),
                Name = "Warm tailwind microphones",
                Kind = ReceiverRigKind.StereoMicrophone,
                Transform = new Transform { PositionMetres = new(30, 4, 0) },
                SpacingMetres = 0.1,
                PolarPattern = PolarPattern.Omni
            },
            new ReceiverRig
            {
                Id = Guid.Parse("eeeeeeee-0000-4000-8000-000000000002"),
                Name = "Cold headwind microphones",
                Kind = ReceiverRigKind.StereoMicrophone,
                Transform = new Transform { PositionMetres = new(30, 10, 0) },
                SpacingMetres = 0.1,
                PolarPattern = PolarPattern.Omni
            }
        ]);
        scene.RenderProfiles[0] = scene.RenderProfiles[0] with
        {
            Name = "Final layered atmosphere 48k",
            Quality = RenderQuality.Final,
            ReflectionOrder = 0,
            RayCount = 0,
            ImpulseResponseSeconds = 0,
            AtmosphereIntegrationSegments = 48,
            Seed = 161803
        };
        return scene with
        {
            ProjectId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000000"),
            Name = "Layered Atmosphere Transit",
            Atmosphere = new AtmosphereField
            {
                TemperatureCelsius = 20,
                RelativeHumidityPercent = 50,
                PressureKilopascals = 101.325,
                Layers =
                [
                    new AtmosphereLayer
                    {
                        BaseAltitudeMetres = 0,
                        TopAltitudeMetres = 6.5,
                        TemperatureCelsius = 32,
                        RelativeHumidityPercent = 80,
                        PressureKilopascals = 100
                    },
                    new AtmosphereLayer
                    {
                        BaseAltitudeMetres = 7.5,
                        TopAltitudeMetres = 14,
                        TemperatureCelsius = 5,
                        RelativeHumidityPercent = 25,
                        PressureKilopascals = 90
                    }
                ],
                WindZones =
                [
                    new WindZone
                    {
                        Id = Guid.Parse("eeeeeeee-0000-4000-8000-000000000003"),
                        Name = "Warm tailwind corridor",
                        Transform = new Transform { PositionMetres = new(0, 4, 0) },
                        DimensionsMetres = new(70, 5, 10),
                        VelocityMetresPerSecond = new(15, 0, 0),
                        Turbulence = 0.15,
                        NoiseSeed = 101
                    },
                    new WindZone
                    {
                        Id = Guid.Parse("eeeeeeee-0000-4000-8000-000000000004"),
                        Name = "Cold headwind corridor",
                        Transform = new Transform { PositionMetres = new(0, 10, 0) },
                        DimensionsMetres = new(70, 5, 10),
                        VelocityMetresPerSecond = new(-10, 0, 0),
                        Turbulence = 0.05,
                        NoiseSeed = 202
                    }
                ]
            },
            Timeline = new EventTimeline
            {
                DurationSeconds = 2,
                Events =
                [
                    new TimelineEvent { Id = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005"), Kind = TimelineEventKind.StartEmitter, TimeSeconds = 0, TargetId = scene.Emitters[0].Id },
                    new TimelineEvent { Id = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006"), Kind = TimelineEventKind.EndCapture, TimeSeconds = 2, Label = "End atmosphere comparison" }
                ]
            },
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)
        };
    }
}
