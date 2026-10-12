using System;
namespace ScreenCompanion
{
    internal sealed class WallpaperMatting
    {
        internal double Scale;
        internal readonly double[] Offset=new double[3];
        internal static WallpaperMatting Fit(byte[] pixels,byte[] wallpaper,int width,int height)
        {
            if(width<2 || height<12 || pixels.Length!=wallpaper.Length)return null;
            double[] meanPixel=new double[3],meanBackground=new double[3];int count=0;
            for(int x=0;x<width;x++)for(int edge=0;edge<2;edge++)for(int row=2;row<=4;row++)
            {
                int y=edge==0?row:height-1-row,i=(y*width+x)*4;
                for(int c=0;c<3;c++){meanPixel[c]+=pixels[i+c];meanBackground[c]+=wallpaper[i+c];}count++;
            }
            for(int c=0;c<3;c++){meanPixel[c]/=count;meanBackground[c]/=count;}
            double numerator=0,denominator=0;
            for(int x=0;x<width;x++)for(int edge=0;edge<2;edge++)for(int row=2;row<=4;row++)
            {
                int i=((edge==0?row:height-1-row)*width+x)*4;
                for(int c=0;c<3;c++){double b=wallpaper[i+c]-meanBackground[c];numerator+=b*(pixels[i+c]-meanPixel[c]);denominator+=b*b;}
            }
            double scale=denominator<count*3?1:numerator/denominator;
            // Do not apply a wallpaper reference when an app/dynamic background
            // is visible behind the taskbar, or an opaque bar rejects its texture.
            if(scale<0.25 || scale>1.08)return null;
            var model=new WallpaperMatting{Scale=Math.Min(1,scale)};
            for(int c=0;c<3;c++)model.Offset[c]=meanPixel[c]-model.Scale*meanBackground[c];
            double error=0;int wrong=0;
            for(int x=0;x<width;x++)for(int edge=0;edge<2;edge++)for(int row=2;row<=4;row++)
            {
                int i=((edge==0?row:height-1-row)*width+x)*4;double maximum=0;
                for(int c=0;c<3;c++){double difference=Math.Abs(pixels[i+c]-model.Color(wallpaper[i+c],c));error+=difference;maximum=Math.Max(maximum,difference);}
                if(maximum>20)wrong++;
            }
            return error/(count*3)<=7 && wrong<=count/10?model:null;
        }
        internal double Color(byte component,int channel){return Math.Max(0,Math.Min(255,component*Scale+Offset[channel]));}
    }
}
