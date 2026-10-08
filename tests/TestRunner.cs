using System;
using System.Reflection;

namespace ScreenSwitch.Tests
{
    public static class Assert
    {
        public static void True(bool cond, string msg) { if (!cond) throw new Exception("Assert.True failed: " + msg); }
        public static void Eq(object expected, object actual, string msg)
        {
            if (!object.Equals(expected, actual)) throw new Exception(msg + ": expected <" + expected + "> got <" + actual + ">");
        }
        public static void Throws<T>(Action a, string msg) where T : Exception
        {
            try { a(); } catch (T) { return; }
            throw new Exception("Assert.Throws failed, no " + typeof(T).Name + ": " + msg);
        }
    }

    public static class TestRunner
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Tr.Use("zh");
            int pass = 0, fail = 0;
            foreach (Type t in Assembly.GetExecutingAssembly().GetTypes())
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!m.Name.StartsWith("Test_")) continue;
                    try { m.Invoke(null, null); pass++; Console.WriteLine("PASS " + t.Name + "." + m.Name); }
                    catch (Exception e)
                    {
                        fail++;
                        Exception inner = e.InnerException ?? e;
                        Console.WriteLine("FAIL " + t.Name + "." + m.Name + ": " + inner.Message);
                    }
                }
            }
            Console.WriteLine(pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }
    }
}
