using System;
using System.Diagnostics;
using System.Threading;

namespace ScreenCompanion
{
    internal static class KeyboardCheck
    {
        [STAThread] static int Main()
        {
            try
            {
                var g=new WinKeyGesture();
                Check(!g.Update(0x5B,true,false) && g.Update(0x5B,false,true),"left Win tap");
                Check(!g.Update(0x5C,true,false) && g.Update(0x5C,false,true),"right Win tap");
                Check(!g.Update(0x5B,false,true),"orphan key-up ignored");
                foreach(uint key in new uint[]{0x45,0x44,0x53,0x52,0x25,0x27})
                {
                    g.Update(0x5B,true,false);g.Update(key,true,false);g.Update(key,false,true);
                    Check(!g.Update(0x5B,false,true),"Win chord excluded: "+key.ToString("X"));
                }
                foreach(uint modifier in new uint[]{0xA0,0xA2,0xA4})
                {
                    g.Update(modifier,true,false);g.Update(0x5B,true,false);
                    Check(!g.Update(0x5B,false,true),"pre-held modifier excluded");g.Update(modifier,false,true);
                }
                g.Update(0x5B,true,false);g.Update(0x5B,true,false);
                Check(g.Update(0x5B,false,true),"repeat down produces one tap");
                g.Update(0x5B,true,false);g.Cancel();Check(!g.Update(0x5B,false,true),"modifier state synchronization");
                for(int cycle=0;cycle<5;cycle++)
                {
                    using(var input=new KeyboardListener(delegate{}))
                    {
                        bool passed=false;long latency=0;
                        var worker=new Thread(delegate(){Thread.Sleep(30);var watch=Stopwatch.StartNew();passed=input.Responsive(150);latency=watch.ElapsedMilliseconds;});
                        worker.Start();Thread.Sleep(400);worker.Join();
                        Check(passed,"native keyboard pump responsive during 400 ms owner/UI stall, cycle "+cycle+", latency "+latency+" ms");
                        input.Dispose();
                    }
                }
                Console.WriteLine("PASS: keyboard gesture and native input-pump checks; no keyboard input injected");return 0;
            }
            catch(Exception e){Console.WriteLine("FAIL: "+e);return 1;}
        }
        static void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS: "+label);}
    }
}
