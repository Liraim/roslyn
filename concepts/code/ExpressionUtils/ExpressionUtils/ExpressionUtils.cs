﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;


// https://dlr.codeplex.com/

namespace ExpressionUtils
{
    using static Utils;

   

    using Env = Func<Exp, ParameterExpression>;

  
    public abstract class Exp {

    }

    public abstract class Exp<T> : Exp {

        public abstract Expression Translate(Env E);

        public Func<T> Compile() {
            var c = this.Translate(Empty);
            var l = Expression.Lambda<Func<T>>(c);
            return l.Compile();
        }

        public T Run () => Compile()();
        
    }


    public class Constant<T> : Exp<T> {
        public T c;
        public Constant(T c) {
            this.c = c;
        }

        public override Expression Translate(Env E) {
            return Expression.Constant(c);
        }
    }
    public class Var<T> : Exp<T> {
        public Var() {

        }

        public override Expression Translate(Env E) {
            return  E(this);
        }
    }

    public class Lam<T,U> : Exp<Func<T,U>> {
        public Var<T> v;
        public Exp<U> e;
        public Lam(Func<Var<T>,Exp<U>> f) {
            this.v = new Var<T>();
            this.e = f(v);
        }

        public override Expression Translate(Env E) {
            var p = Expression.Parameter(typeof(T));
            var Ex = E.Add(v, p);
            return Expression.Lambda(e.Translate(Ex), p);
        }
    }

    public class App<T,U> : Exp<U> {
        public Exp<Func<T, U>> f;
        public Exp<T> e;
        public App(Exp<Func<T,U>> f, Exp<T> e) {
            this.f = f;
            this.e = e;
        }

        public override Expression Translate(Env E) {
            return Expression.Invoke(f.Translate(E), e.Translate(E));
        }
    }



    public class Let<T, U> : Exp<U> {
        public Var<T> x;
        public Exp<T> e;
        public Exp<U> f;
        public Let(Exp<T> e, Func<Var<T>, Exp<U>> f ) {
            this.x = new Var<T>();
            this.e = e;
            this.f = f(x);
        }

        public override Expression Translate(Env E) {
            var p = Expression.Parameter(typeof(T));
            var ce = e.Translate(E);
            var Ex = E.Add(x, p);
            var fc = f.Translate(Ex);
            return Expression.Block(new [] { p }, Expression.Assign(p, e.Translate(E)),fc);           
        }
    }


    public class Prim<T1, T> : Exp<T> {

        public Expression<Func<T1, T>> f;
        public Exp<T1> e1;
       
        public Prim(Expression<Func<T1, T>> f, Exp<T1> e1) {
            this.f = f;
            this.e1 = e1;
        }

        public override Expression Translate(Env E) {
            var p = f.Parameters[0];
            var c1 = e1.Translate(E);
            return Expression.Block(new[] { p },
                                    Expression.Assign(p, c1),
                                    f.Body);
        }
    }

    public class Prim<T1,T2,T> : Exp<T> {

        public Expression<Func<T1, T2, T>> f;
        public Exp<T1> e1;
        public Exp<T2> e2;
        public Prim(Expression<Func<T1, T2, T>> f, Exp<T1> e1, Exp<T2> e2) {
            this.f = f;
            this.e1 = e1;
            this.e2 = e2;
        }

        public override Expression Translate(Env E) {
            var p = f.Parameters[0];
            var q = f.Parameters[1];
            var c1 = e1.Translate(E);
            var c2 = e2.Translate(E);
            return Expression.Block(new[] { p, q }, 
                                    Expression.Assign(p, c1),
                                    Expression.Assign(q, c2),
                                    f.Body);
        }
    }



    public static class Utils {

        public static Env Empty = x => { throw new System.ArgumentOutOfRangeException(); };

        public static Env Add(this Env E, Exp x, ParameterExpression p) =>
                       (y) => (x == y) ? p : E(x);

        public static Exp<T> C<T>(T t) => new Constant<T>(t);
        public static Exp<Func<T,U>> Lam<T,U>(Func<Var<T>, Exp<U>> f) =>
               new Lam<T,U>(f);


        public static Exp<U> Let<T, U>(Exp<T> e, Func<Var<T>, Exp<U>> f) =>
               new Let<T, U>(e, f);

        public static Exp<U> Apply<T, U>(this Exp<Func<T, U>> f, Exp<T> e) =>
              new App<T, U>(f, e);

        public static Exp<T> Prim<T1,T2,T>(Expression<Func<T1, T2, T>> f, Exp<T1> e1, Exp<T2> e2) =>
             new Prim<T1,T2,T>(f, e1, e2);
        public static Exp<T> Prim<T1, T>(Expression<Func<T1, T>> f, Exp<T1> e1) =>
             new Prim<T1,T>(f, e1);
    }


    public static class Test {
        static void Main() {

            var t1 = C(1);
            var r1 = t1.Run();

            var t2 = Lam<int,int>(x => x);
            var r2 = t2.Run();

            var t3 = t2.Apply(t1);
            var r3 = t3.Run();


            var t4 = Let(t1, x => x);
            var r4 = t4.Run();

            var t6 = Let(t1, x =>  t2.Apply(x));
            var r6 = t6.Run();


            var t7 = Prim(x=>Math.Cos(x),C(0.0) );

            var r7 = t7.Run();

            var t8 = Prim((x, y) => x + y, C(1), C(2));

            var r8 = t8.Run();


            System.Console.WriteLine();




        }



    }


}
