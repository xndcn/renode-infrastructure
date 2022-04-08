//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class ExternalMmuBase: IPeripheral
    {
        public ExternalMmuBase(ICPUWithExternalMmu cpu, uint windowsCount, MmuType type)
        {
            this.cpu = cpu;
            this.windowsCount = windowsCount;
            IRQ = new GPIO();
            windowMapping = new Dictionary<uint, uint>();

            RegisterInCpu(cpu, type);
            for(uint index = 0; index < windowsCount; index++)
            {
                AddWindow(index);
            }
        }

        public virtual void Reset()
        {
            foreach(var realIndex in windowMapping.Values)
            {
                cpu.ResetMmuWindow(realIndex);
            }
            windowMapping = new Dictionary<uint, uint>();
        }

        public void SetWindowStart(uint index, ulong start_addr)
        {
            cpu.SetMmuWindowStart(RealWindowIndex(index), start_addr);
        }

        public void SetWindowEnd(uint index, ulong end_addr)
        {
            cpu.SetMmuWindowEnd(RealWindowIndex(index), end_addr);
        }

        public ulong GetWindowStart(uint index)
        {
            return cpu.GetMmuWindowStart(RealWindowIndex(index));
        }

        public ulong GetWindowEnd(uint index)
        {
            return cpu.GetMmuWindowEnd(RealWindowIndex(index));
        }

        public void SetWindowAddend(uint index, ulong addend)
        {
            cpu.SetMmuWindowAddend(RealWindowIndex(index), addend);
        }

        public void SetWindowPrivileges(uint index, uint privileges)
        {
            cpu.SetMmuWindowPrivileges(RealWindowIndex(index), (uint)privileges);
        }

        public ulong GetWindowAddend(uint index)
        {
            return cpu.GetMmuWindowAddend(RealWindowIndex(index));
        }

        public uint GetWindowPrivileges(uint index)
        {
            return cpu.GetMmuWindowPrivileges(RealWindowIndex(index));
        }

        public uint RealWindowIndex(uint value)
        {
            if(value >= windowsCount)
            {
                this.Log(LogLevel.Error, "Window index {0} is higher than the peripheral windows count: {1}", value, windowsCount);
            }
            return windowMapping[value];
        }

        public GPIO IRQ { get; }

        private void RegisterInCpu(ICPUWithExternalMmu cpu, MmuType type)
        {
            cpu.AttachMmu(this, type);
            cpu.EnableExternalWindowMmu(true);
        }

        private void AddWindow(uint index, ulong? rangeStart = null, ulong? rangeEnd = null, ulong? addend = null, Privilege? privilege = null)
        {
            var realIndex = cpu.AcquireExternalMmuWindow();
            if(realIndex == -1)
            {
                throw new ConstructionException("Failed to acquire the MMU window. Possibly ran out of windows");
            }
            windowMapping.Add(index, realIndex);

            if(rangeStart.HasValue)
            {
                cpu.SetMmuWindowStart(realIndex, rangeStart.Value);
            }
            if(rangeEnd.HasValue)
            {
                cpu.SetMmuWindowEnd(realIndex, rangeEnd.Value);
            }
            if(addend.HasValue)
            {
                cpu.SetMmuWindowAddend(realIndex, addend.Value);
            }
            if(privilege.HasValue)
            {
                cpu.SetMmuWindowPrivileges(realIndex, (uint)privilege.Value);
            }
        }

        public void TriggerInterrupt()
        {
            this.Log(LogLevel.Debug, "MMU fault occured. Setting the IRQ");
            IRQ.Set();
        }

        private readonly ICPUWithExternalMmu cpu;
        private readonly uint windowsCount;
        // There might be more than one ExternalMmu for the single CPU, hence the MMU window index is not the CPU MMU window index
        private Dictionary<uint, uint> windowMapping;

        public enum Privilege
        {
            Read = 0b001,
            Write = 0b010,
            ReadAndWrite = 0b011,
            Execute = 0b100,
            All = 0b111,
        }
    }
}
