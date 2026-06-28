using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CASCEdit.Helpers;

namespace CASCEdit.Structs
{
    // WoW Classic 3.4.3 download manifest (version 1) only.
    public class DownloadHeader
    {
        public byte[] Header = new byte[] { 68, 76 }; // DL
        public byte Version = 1;
        public byte ChecksumSize = 16;
        public byte HasChecksum = 1;
        public uint NumEntries;
        public ushort NumTags;
	}

    public class DownloadEntry
    {
        public MD5Hash EKey;
        public ulong FileSize;
        public byte Priority;
		public uint Checksum;
	}

    public class DownloadTag
    {
        public string Name;
        public ushort Type;
        public BoolArray BitMask;
    }
}
