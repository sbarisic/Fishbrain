namespace Fishbrain;

/// <summary>A scalar value and the tiny reverse-mode autograd graph behind it.</summary>
internal sealed class Value
{
	[ThreadStatic]
	private static int _noGradDepth;
	private readonly Value[] _children;
	private readonly double[] _localGrads;

	public Value(double data)
		: this(data, [], [])
	{
	}

	private Value(double data, Value[] children, double[] localGrads)
	{
		Data = data;
		_children = children;
		_localGrads = localGrads;
	}

	public double Data { get; set; }
	public double Grad { get; set; }

	public static IDisposable NoGrad()
	{
		_noGradDepth++;
		return new RecordingScope();
	}

	public static Value operator +(Value a, Value b) =>
		Create(a.Data + b.Data, [a, b], [1.0, 1.0]);

	public static Value operator +(Value a, double b) => a + new Value(b);
	public static Value operator +(double a, Value b) => new Value(a) + b;

	public static Value operator *(Value a, Value b) =>
		Create(a.Data * b.Data, [a, b], [b.Data, a.Data]);

	public static Value operator *(Value a, double b) => a * new Value(b);
	public static Value operator *(double a, Value b) => new Value(a) * b;
	public static Value operator -(Value a) => a * -1.0;
	public static Value operator -(Value a, Value b) => a + -b;
	public static Value operator -(Value a, double b) => a + -b;
	public static Value operator -(double a, Value b) => a + -b;
	public static Value operator /(Value a, Value b) => a * b.Pow(-1.0);
	public static Value operator /(Value a, double b) => a * Math.Pow(b, -1.0);
	public static Value operator /(double a, Value b) => a * b.Pow(-1.0);

	public Value Pow(double exponent) =>
		Create(Math.Pow(Data, exponent), [this], [exponent * Math.Pow(Data, exponent - 1.0)]);

	public Value Log() => Create(Math.Log(Data), [this], [1.0 / Data]);

	public Value Exp()
	{
		var result = Math.Exp(Data);
		return Create(result, [this], [result]);
	}

	public Value Relu() => Create(Math.Max(0.0, Data), [this], [Data > 0.0 ? 1.0 : 0.0]);

	/// <summary>
	/// Stable fused softmax cross-entropy. One graph node replaces the individual
	/// exponent, sum, and logarithm nodes while retaining exact logit gradients.
	/// </summary>
	public static Value CrossEntropy(IReadOnlyList<Value> logits, int target)
	{
		if (logits.Count == 0) throw new ArgumentException("Cross-entropy requires logits.", nameof(logits));
		if ((uint)target >= (uint)logits.Count) throw new ArgumentOutOfRangeException(nameof(target));

		var maximum = logits.Max(value => value.Data);
		var exponentials = new double[logits.Count];
		var sum = 0.0;
		for (var index = 0; index < logits.Count; index++)
		{
			var exponential = Math.Exp(logits[index].Data - maximum);
			exponentials[index] = exponential;
			sum += exponential;
		}

		var data = Math.Log(sum) + maximum - logits[target].Data;
		if (_noGradDepth > 0) return new Value(data);

		var children = logits.ToArray();
		var localGrads = new double[children.Length];
		for (var index = 0; index < localGrads.Length; index++)
			localGrads[index] = exponentials[index] / sum;
		localGrads[target] -= 1.0;
		return new Value(data, children, localGrads);
	}

	/// <summary>
	/// A fused dot-product node. It keeps the graph compact while retaining exact gradients
	/// for every input scalar.
	/// </summary>
	public static Value Dot(
		IReadOnlyList<Value> a,
		int aStart,
		IReadOnlyList<Value> b,
		int bStart,
		int count)
	{
		if (count < 0 || aStart < 0 || bStart < 0 ||
			aStart + count > a.Count || bStart + count > b.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(count));
		}

		var data = 0.0;
		if (_noGradDepth > 0)
		{
			for (var i = 0; i < count; i++) data += a[aStart + i].Data * b[bStart + i].Data;
			return new Value(data);
		}

		var children = new Value[count * 2];
		var localGrads = new double[count * 2];
		for (var i = 0; i < count; i++)
		{
			var av = a[aStart + i];
			var bv = b[bStart + i];
			data += av.Data * bv.Data;
			children[i] = av;
			children[count + i] = bv;
			localGrads[i] = bv.Data;
			localGrads[count + i] = av.Data;
		}

		return new Value(data, children, localGrads);
	}

	public static Value Dot(IReadOnlyList<Value> a, IReadOnlyList<Value> b)
	{
		if (a.Count != b.Count) throw new ArgumentException("Dot-product inputs must have equal lengths.");
		return Dot(a, 0, b, 0, a.Count);
	}

	public void Backward()
	{
		var topo = new List<Value>();
		var visited = new HashSet<Value> { this };
		var stack = new Stack<(Value Node, bool Expanded)>();
		stack.Push((this, false));

		while (stack.Count > 0)
		{
			var (node, expanded) = stack.Pop();
			if (expanded)
			{
				topo.Add(node);
				continue;
			}

			stack.Push((node, true));
			for (var i = node._children.Length - 1; i >= 0; i--)
			{
				var child = node._children[i];
				if (visited.Add(child)) stack.Push((child, false));
			}
		}

		Grad = 1.0;
		for (var i = topo.Count - 1; i >= 0; i--)
		{
			var node = topo[i];
			if (node.Grad == 0.0) continue;
			for (var j = 0; j < node._children.Length; j++)
			{
				node._children[j].Grad += node._localGrads[j] * node.Grad;
			}
		}
	}

	private static Value Create(double data, Value[] children, double[] localGrads) =>
		_noGradDepth == 0 ? new Value(data, children, localGrads) : new Value(data);

	private sealed class RecordingScope : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed) return;
			if (_noGradDepth <= 0) throw new InvalidOperationException("No-gradient scope underflow.");
			_noGradDepth--;
			_disposed = true;
		}
	}
}
