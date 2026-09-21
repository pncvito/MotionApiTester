using System;
using System.Collections.Generic;

namespace MotionApiTester.Models
{
    /// <summary>设备 Profile</summary>
    public class DeviceProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name { get; set; } = "";
        public string Directory { get; set; } = "";
        public string BaseTesterPath => System.IO.Path.Combine(Directory, "OptoFidelity.BaseTester.dll");
        public string ModelDllPath => string.IsNullOrEmpty(ModelDllName) ? "" : System.IO.Path.Combine(Directory, ModelDllName);
        public string ModelDllName { get; set; } = "";
        public string MachineType { get; set; } = "";

        /// <summary>备注。设备向导里填的说明文字（以前那次输入没地方存，填了等于没填）</summary>
        public string Note { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUsedAt { get; set; } = DateTime.Now;
    }
}
