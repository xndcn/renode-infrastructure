//
// Copyright (c) 2010-2023 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Time;
using Antmicro.Renode.Logging;
using Antmicro.Migrant;
using Antmicro.Migrant.Hooks;

namespace Antmicro.Renode.Utilities.RESD
{
    public enum RESDStreamStatus
    {
        // Provided sample is from requested timestamp
        OK = 0,
        // There are no samples for given samples in current block yet
        BeforeStream = -1,
        // There are no more samples of given type in the block/file
        AfterStream = -2,
    }

    public enum RESDStreamSampleOffset
    {
        // Use specified sample offset
        Specified,
        // User current virtual-time timestamp as offset
        CurrentVirtualTime,
    }

    public static class RESDStreamExtension
    {
        public static RESDStream<T> CreateRESDStream<T>(this IPeripheral @this, ReadFilePath path, uint channel,
            RESDStreamSampleOffset offsetType = RESDStreamSampleOffset.Specified, long sampleOffsetTime = 0) where T: RESDSample, new()
        {
            if(offsetType == RESDStreamSampleOffset.CurrentVirtualTime)
            {
                var machine = @this.GetMachine();
                if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
                {
                    cpu.SyncTime();
                }
                sampleOffsetTime = (long)machine.ClockSource.CurrentValue.TotalMicroseconds * -1000L;
            }

            var stream = new RESDStream<T>(path, channel, sampleOffsetTime);
            stream.Owner = @this;
            return stream;
        }

        public static RESDStreamStatus TryGetCurrentSample<T, Out>(this RESDStream<T> @this, IPeripheral peripheral, Func<T, Out> transformer,
            out Out sample, out TimeInterval timestamp, long? overrideSampleOffsetTime = null) where T: RESDSample, new()
        {
            var result = @this.TryGetCurrentSample(peripheral, out var originalSample, out timestamp, overrideSampleOffsetTime);
            sample = transformer(originalSample);
            return result;
        }
    }

    public class RESDStream<T, Out> : RESDStream<T> where T: RESDSample, new()
    {
        public RESDStream(ReadFilePath path, uint channel, Func<T, Out> transformer, long sampleOffsetTime = 0) : base(path, channel, sampleOffsetTime)
        {
            this.transformer = transformer;
        }

        public RESDStreamStatus TryGetSample(ulong timestamp, out Out sample, long? overrideSampleOffsetTime = null)
        {
            var result = TryGetSample(timestamp, out T originalSample, overrideSampleOffsetTime);
            sample = transformer(originalSample);
            return result;
        }

        public RESDStreamStatus TryGetCurrentSample(IPeripheral peripheral, out Out sample, out TimeInterval timestamp, long? overrideSampleOffsetTime = null)
        {
            var result = TryGetCurrentSample(peripheral, out T originalSample, out timestamp, overrideSampleOffsetTime);
            sample = transformer(originalSample);
            return result;
        }

        public IManagedThread StartSampleFeedThread(IPeripheral owner, uint frequency, Action<Out, TimeInterval, RESDStreamStatus> newSampleCallback, ulong startTime = 0, long? overrideSampleOffsetTime = null)
        {
            Action<T, TimeInterval, RESDStreamStatus> transformedCallback =
                (sample, timestamp, status) => newSampleCallback(transformer(sample), timestamp, status);
            return StartSampleFeedThread(owner, frequency, transformedCallback, startTime, overrideSampleOffsetTime);
        }

        private readonly Func<T, Out> transformer;
    }

    public class RESDStream<T> : IDisposable where T : RESDSample, new()
    {
        public RESDStream(ReadFilePath path, uint channel, long sampleOffsetTime = 0)
        {
            var sampleTypeAttribute = typeof(T).GetCustomAttributes(typeof(SampleTypeAttribute), true).FirstOrDefault() as SampleTypeAttribute;
            if(sampleTypeAttribute == null)
            {
                throw new RESDException($"Unsupported RESD sample type: {typeof(T).Name}");
            }

            this.channel = channel;
            this.sampleOffsetTime = sampleOffsetTime;
            this.managedThreads = new List<IManagedThread>();
            this.parser = new LowLevelRESDParser(path);
            this.parser.LogCallback += (logLevel, message) => Owner?.Log(logLevel, message);
            this.blockEnumerator = parser.GetDataBlockEnumerator<T>().GetEnumerator();

            PrereadFirstBlock();
        }

        public RESDStreamStatus TryGetCurrentSample(IPeripheral peripheral, out T sample, out TimeInterval timestamp, long? overrideSampleOffsetTime = null)
        {
            var machine = peripheral.GetMachine();
            timestamp = machine.ClockSource.CurrentValue;
            var timestampInMicroseconds = timestamp.TotalMicroseconds * 1000;
            return TryGetSample(timestampInMicroseconds, out sample, overrideSampleOffsetTime);
        }

        public RESDStreamStatus TryGetSample(ulong timestamp, out T sample, long? overrideSampleOffsetTime = null)
        {
            if(timestamp < currentBlock.StartTime)
            {
                Owner?.Log(LogLevel.Debug, "RESD: Tried getting sample at timestamp {0}ns, before the start time of the current block", timestamp);
                sample = null;
                return RESDStreamStatus.BeforeStream;
            }

            var currentSampleOffsetTime = overrideSampleOffsetTime ?? sampleOffsetTime;
            timestamp = currentSampleOffsetTime > 0 ? timestamp + (ulong)currentSampleOffsetTime : timestamp - (ulong)(-currentSampleOffsetTime);

            while(blockEnumerator != null)
            {
                if(currentBlock == null)
                {
                    if(!TryGetNextBlock(out currentBlock))
                    {
                        break;
                    }
                    MetadataChanged?.Invoke();
                }

                switch(currentBlock.TryGetSample(timestamp, out sample))
                {
                    case RESDStreamStatus.BeforeStream:
                        Owner?.Log(LogLevel.Debug, "RESD: Tried getting sample at timestamp {0}ns, before the first sample in the block", timestamp);
                        sample = null;
                        return RESDStreamStatus.BeforeStream;
                    case RESDStreamStatus.OK:
                        // Just return sample
                        Owner?.Log(LogLevel.Debug, "RESD: Getting sample at timestamp {0}ns: {1}", timestamp, sample);
                        return RESDStreamStatus.OK;
                    case RESDStreamStatus.AfterStream:
                        // Find next block
                        Owner?.Log(LogLevel.Debug, "RESD: Tried getting sample at timestamp {0}ns after the last sample of the current block", timestamp);
                        currentBlock = null;
                        continue;
                }

                return RESDStreamStatus.OK;
            }

            Owner?.Log(LogLevel.Debug, "RESD: That was the last block of the file");
            sample = null;
            return RESDStreamStatus.AfterStream;
        }

        public IManagedThread StartSampleFeedThread(IPeripheral owner, uint frequency, Action<T, TimeInterval, RESDStreamStatus> newSampleCallback, ulong startTime = 0, long? overrideSampleOffsetTime = null)
        {
            var machine = owner.GetMachine();
            Action feedSample = () =>
            {
                var status = TryGetCurrentSample(owner, out var sample, out var timestamp, overrideSampleOffsetTime);
                newSampleCallback(sample, timestamp, status);
            };

            Func<bool> stopCondition = () =>
            {
                if(blockEnumerator == null)
                {
                    Owner?.Log(LogLevel.Debug, "RESD: End of sample feeding thread detected");
                    newSampleCallback(null, TimeInterval.Empty, RESDStreamStatus.AfterStream);
                    return true;
                }
                return false;
            };

            var thread = machine.ObtainManagedThread(feedSample, frequency, "RESD stream thread", owner, stopCondition);
            var delayInterval = TimeInterval.FromMicroseconds(startTime / 1000);
            Owner?.Log(LogLevel.Debug, "RESD: Starting samples feeding thread at frequency {0}Hz delayed by {0}us", frequency, delayInterval);
            thread.StartDelayed(delayInterval);
            managedThreads.Add(thread);
            return thread;
        }

        public void Dispose()
        {
            foreach(var thread in managedThreads)
            {
                thread.Dispose();
            }
            managedThreads.Clear();
            blockEnumerator?.Dispose();
        }

        // The `Owner` property is used to log detailed messages
        // about the process of the RESD file parsing.
        // If it's not set (set to `null`, by default) no log messages
        // will be generated.
        public IEmulationElement Owner { get; set; }

        public T CurrentSample => currentBlock?.CurrentSample;
        public Action MetadataChanged;

        private void PrereadFirstBlock()
        {
            if(!TryGetNextBlock(out currentBlock))
            {
                throw new RESDException($"Provided RESD file doesn't contain data for {typeof(T)}");
            }
            Owner?.Log(LogLevel.Debug, "RESD: First sample of the file has timestamp {0}ns", currentBlock.StartTime);
        }

        private bool TryGetNextBlock(out DataBlock<T> block)
        {
            if(blockEnumerator == null)
            {
                block = null;
                return false;
            }

            while(blockEnumerator.TryGetNext(out var nextBlock))
            {
                if(nextBlock.ChannelId != this.channel)
                {
                    Owner?.Log(LogLevel.Debug, "RESD: Skipping block of type {0} and size {1} bytes", nextBlock.BlockType, nextBlock.DataSize);
                    continue;
                }

                block = nextBlock;
                return true;
            }

            block = null;
            blockEnumerator = null;
            return false;
        }

        [PreSerialization]
        private void BeforeSerialization()
        {
            serializedTimestamp = currentBlock.CurrentTimestamp;
        }

        [PostDeserialization]
        private void AfterDeserialization()
        {
            PrereadFirstBlock();
            TryGetSample(serializedTimestamp, out _);
        }

        [Transient]
        private DataBlock<T> currentBlock;
        [Transient]
        private IEnumerator<DataBlock<T>> blockEnumerator;
        private ulong serializedTimestamp;
        private long sampleOffsetTime;

        private readonly LowLevelRESDParser parser;
        private readonly uint channel;
        private readonly IList<IManagedThread> managedThreads;
    }
}