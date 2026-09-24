using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;

public static class NativeDiskMark {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc callback, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr ReadMessage(IntPtr h, uint msg, IntPtr w, StringBuilder s);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr h);
    public class Control { public IntPtr Handle; public int Id; public string Class; public string Text; }
    public static Control[] Children(IntPtr parent) {
        List<Control> result = new List<Control>();
        EnumProc callback = delegate(IntPtr h, IntPtr p) {
            StringBuilder text = new StringBuilder(2048); StringBuilder cls = new StringBuilder(256);
            GetWindowText(h,text,text.Capacity); GetClassName(h,cls,cls.Capacity);
            Control control = new Control();
            control.Handle=h; control.Id=GetDlgCtrlID(h); control.Class=cls.ToString(); control.Text=text.ToString();
            result.Add(control);
            return true;
        };
        EnumChildWindows(parent, callback, IntPtr.Zero);
        return result.ToArray();
    }
    public static string Item(IntPtr combo, int index) {
        int length = (int)SendMessage(combo,0x149,new IntPtr(index),IntPtr.Zero);
        if(length < 0 || length > 8192) throw new Exception("Cannot read disk selector");
        StringBuilder s = new StringBuilder(length+1);
        ReadMessage(combo,0x148,new IntPtr(index),s);
        return s.ToString();
    }
    public static void Select(IntPtr parent, IntPtr combo, int index) {
        if((int)SendMessage(combo,0x14E,new IntPtr(index),IntPtr.Zero) != index)
            throw new Exception("Cannot select benchmark setting");
        SendMessage(parent,0x111,new IntPtr((1<<16)|GetDlgCtrlID(combo)),combo);
    }
}
