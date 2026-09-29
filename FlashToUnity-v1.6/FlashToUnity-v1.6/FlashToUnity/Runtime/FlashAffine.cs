using UnityEngine;
namespace FlashToUnity.Runtime
{
    // A two-dimensional SVD: R(left) * Scale(signed singular values) * R(right).
    // Unlike Transform.lossyScale this preserves shear and reflections exactly.
    public static class FlashAffine
    {
        public static void Decompose(Matrix4x4 m, out Vector2 position, out Vector2 scale, out float left, out float right)
        {
            position = new Vector2(m.m03,m.m13);
            double a=m.m00,b=m.m01,c=m.m10,d=m.m11;
            double angle=.5*System.Math.Atan2(2*(a*c+b*d),a*a+b*b-c*c-d*d);
            double co=System.Math.Cos(angle),si=System.Math.Sin(angle);
            double x=co*a+si*c,y=co*b+si*d;
            double s=System.Math.Sqrt(x*x+y*y);
            left=(float)(angle*180/System.Math.PI);
            if(s<1e-12) { scale=Vector2.zero;right=0;return; }
            right=(float)(System.Math.Atan2(-y,x)*180/System.Math.PI);
            scale=new Vector2((float)s,(float)((a*d-b*c)/s));
        }
        public static Matrix4x4 Compose(Vector2 position, Vector2 scale, float left, float right)
        {
            return Matrix4x4.TRS(position,Quaternion.Euler(0,0,left),new Vector3(scale.x,scale.y,1))*Matrix4x4.Rotate(Quaternion.Euler(0,0,right));
        }
        public static void Apply(Transform first, Transform second, Transform third, Matrix4x4 matrix)
        {
            Vector2 p,s;float l,r;Decompose(matrix,out p,out s,out l,out r);
            first.localPosition=new Vector3(p.x,p.y,matrix.m23); first.localRotation=Quaternion.Euler(0,0,l);first.localScale=Vector3.one;
            second.localPosition=Vector3.zero;second.localRotation=Quaternion.identity;second.localScale=new Vector3(s.x,s.y,1);
            third.localPosition=Vector3.zero;third.localRotation=Quaternion.Euler(0,0,r);third.localScale=Vector3.one;
        }
    }
}
