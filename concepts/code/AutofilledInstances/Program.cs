﻿using System;

namespace AutofilledInstances
{
    // # Autofilled Instances
    //
    // Sometimes, concept instances are thin wrappers over existing
    // functionality in a type.  Writing instances entirely consisting of
    // `TResult Method(this T1 x, T2 y) => x.Method(y)` would be tedious,
    // so Concept-C# has some support for 'autofilling' such methods.

    class Program
    {
        // Suppose we want to abstract over types that are monoids:

        public concept CMonoid<T>
        {
            T Plus(this T me, T you);
            T Zero { get; }
        }

        // Suppose we also have a class that has a `Plus` method that not only
        // lines up semantically with monoidal append, but in fact has the same
        // signature:

        public struct Pair
        {
            public int x, y;
            public Pair Plus(Pair you) => new Pair { x = x + you.x, y = y + you.y };

            public static Pair operator +(Pair x, Pair y) => x.Plus(y);
        }

        // If we open an instance for CMonoid<Pair>, Concept-C# can automatically
        // detect that CMonoid<Pair>.Plus can be implemented using Pair.Plus, and
        // will create the stub call for us:

        public instance Monoid_Pair : CMonoid<Pair>
        {
            Pair Zero => new Pair { x = 0, y = 0 };
        }

        // We can test this as follows:

        static void TestMonoidPair()
        {
            var p1 = new Pair { x = 30, y = 31 };
            var p2 = new Pair { x = 95, y = 98 };
            Console.WriteLine($"{p1.Plus(p2).x}, {p1.Plus(p2).y}");
        }

        public concept CAdd<X>
        {
            X operator +(X l, X r);
        }
        public instance Add_Pair : CAdd<Pair> {}

        static void TestMonoidAdd()
        {
            var p1 = new Pair { x = 30, y = 31 };
            var p2 = new Pair { x = 95, y = 98 };
            Console.WriteLine($"{(p1 + p2).x}, {(p1 + p2).y}");
        }

        static void Main(string[] args)
        {
            TestMonoidPair();
            TestMonoidAdd();
        }
    }
}
