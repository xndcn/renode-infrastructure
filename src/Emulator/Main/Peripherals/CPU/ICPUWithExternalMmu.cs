//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.CPU
{
    public interface ICPUWithExternalMmu : ICPU
    {
        void EnableExternalWindowMmu(bool value);
        uint AcquireExternalMmuWindow();
        void ResetMmuWindow(uint index);
        void SetMmuWindowStart(uint index, ulong start_addr);
        void SetMmuWindowEnd(uint index, ulong end_addr);
        void SetMmuWindowAddend(uint index, ulong addend);
        void SetMmuWindowPrivileges(uint index, uint privileges);
        void AddHookOnMmuFault(Action<ulong, AccessType, bool> hook);

        ulong GetMmuWindowStart(uint index);
        ulong GetMmuWindowEnd(uint index);
        ulong GetMmuWindowAddend(uint index);
        uint GetMmuWindowPrivileges(uint index);
        void AttachMmu(ExternalMmuBase mmu, MmuType type);

        uint GetExternalMmuWindowsCount { get; }
    }

    public enum AccessType
    {
        Read = 0,
        Write = 1,
        Execute = 2,
    }

    public enum MmuType
    {
        InstructionsOnly = 0,
        General = 1,
    }
}
