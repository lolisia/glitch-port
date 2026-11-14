using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlitchPort
{
    /// <summary>
    /// Windows job object helpers that tie the lifetime of the terminal child to
    /// this process. Every member is best effort: a failure is ignored and never
    /// thrown, because the relay must keep working without the job object.
    /// </summary>
    internal static class WindowsJobObject
    {
        /// <summary>
        /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the last handle to the
        /// job terminates every process assigned to it.
        /// </summary>
        public const uint JobObjectLimitKillOnJobClose = 0x2000;

        private const int JobObjectExtendedLimitInformationClass = 9;

        /// <summary>
        /// Creates a job object limited with <see cref="JobObjectLimitKillOnJobClose"/>
        /// and assigns <paramref name="process"/> to it.
        /// </summary>
        /// <param name="process">The already started child process.</param>
        /// <returns>
        /// The job handle that must be closed with <see cref="Close"/>, or
        /// <see cref="IntPtr.Zero"/> when the job could not be created or assigned.
        /// </returns>
        public static IntPtr TryCreate(Process? process)
        {
            if (process is null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return IntPtr.Zero;
            }

            var jobHandle = IntPtr.Zero;

            try
            {
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var information = new JobObjectExtendedLimitInformation();
                information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

                var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                var buffer = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.StructureToPtr(information, buffer, false);

                    if (!SetInformationJobObject(
                        jobHandle,
                        JobObjectExtendedLimitInformationClass,
                        buffer,
                        (uint)size))
                    {
                        Close(jobHandle);
                        return IntPtr.Zero;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (!AssignProcessToJobObject(jobHandle, process.Handle))
                {
                    Close(jobHandle);
                    return IntPtr.Zero;
                }

                return jobHandle;
            }
            catch (Exception)
            {
                // Job objects are a safety net, not a requirement.
                if (jobHandle != IntPtr.Zero)
                {
                    Close(jobHandle);
                }

                return IntPtr.Zero;
            }
        }

        /// <summary>Closes a job handle created by <see cref="TryCreate"/>; never throws.</summary>
        /// <param name="jobHandle">The job handle, or <see cref="IntPtr.Zero"/>.</param>
        public static void Close(IntPtr jobHandle)
        {
            if (jobHandle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                CloseHandle(jobHandle);
            }
            catch (Exception)
            {
                // Nothing useful can be done when kernel32 is unavailable.
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr jobHandle,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr processHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
