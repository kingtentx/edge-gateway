using System.Globalization;

namespace EdgeGateway.Service;

/// <summary>
/// A deliberately small numeric language. x is the engineering value after scale and offset.
/// It cannot access other points, the file system, .NET members, or executable code.
/// Decimal arithmetic keeps all signed/unsigned 64-bit integer inputs exact. An expression
/// outside the decimal range is an explicit conversion failure, never infinity or a stale value.
/// </summary>
public static class NumericExpression
{
    public const int MaximumLength = 512;
    public const int MaximumNodes = 128;
    public const int MaximumDepth = 32;
    public const int MaximumCachedExpressions = 256;

    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, Node> Cache = new(StringComparer.Ordinal);

    /// <returns>null for valid syntax (including an empty identity expression), otherwise a user-readable error.</returns>
    public static string? Validate(string? expression)
    {
        try { _ = Compile(expression); return null; }
        catch (InvalidDataException exception) { return exception.Message; }
    }

    public static decimal Evaluate(string? expression, decimal x)
    {
        try { return Compile(expression).Evaluate(x); }
        catch (InvalidDataException) { throw; }
        catch (ArithmeticException exception)
        {
            throw new InvalidDataException("计算表达式失败：除数为零，或结果超出 decimal 数值范围。", exception);
        }
    }

    /// <summary>Preview uses the same compiler and evaluator as acquisition, with an invariant engineering input x.</summary>
    public static ExpressionPreview Preview(string? expression, string engineeringValue)
    {
        try { return new(true, Evaluate(expression, ParseEngineeringInput(engineeringValue)), null); }
        catch (InvalidDataException exception) { return new(false, null, exception.Message); }
    }

    /// <summary>Shared by acquisition and preview; decimal parsing must not turn a nonzero input into a valid zero.</summary>
    internal static decimal ParseEngineeringInput(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException("计算表达式的工程输入须为 decimal 范围内的数值，使用小数点作为分隔符。");
        if (value == 0 && HasNonZeroMantissa(text.AsSpan()))
            throw new InvalidDataException("计算表达式的非零工程输入低于 decimal 可表示范围，不能转换为 0。");
        return value;
    }

    private static bool HasNonZeroMantissa(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character is 'e' or 'E') break;
            if (character is >= '1' and <= '9') return true;
        }
        return false;
    }

    private static Node Compile(string? expression)
    {
        if (expression is null or "") return Variable;
        if (expression.Length > MaximumLength)
            throw new InvalidDataException($"计算表达式不能超过 {MaximumLength} 个字符。");
        expression = expression.Trim();
        if (expression.Length == 0) return Variable;
        lock (CacheSync)
            if (Cache.TryGetValue(expression, out var cached)) return cached;
        Node root;
        try
        {
            root = new Parser(expression).Parse();
            // Reject impossible constant subexpressions/arguments without guessing a value for x.
            root.ValidateConstants();
        }
        catch (InvalidDataException) { throw; }
        catch (ArithmeticException exception)
        {
            throw new InvalidDataException("计算表达式常量无效：除数为零，或结果超出 decimal 数值范围。", exception);
        }
        lock (CacheSync)
        {
            // Configurations may be edited repeatedly. Never retain an unlimited process-wide cache.
            if (Cache.Count >= MaximumCachedExpressions) Cache.Clear();
            Cache[expression] = root;
        }
        return root;
    }

    private static readonly Node Variable = new(NodeKind.Variable, 0, []);

    private enum NodeKind
    {
        Constant, Variable, Negate, Add, Subtract, Multiply, Divide, Remainder, Power,
        Abs, Minimum, Maximum, Clamp, Round, Floor, Ceiling, Truncate
    }

    private sealed class Node(NodeKind kind, decimal constant, Node[] children)
    {
        public int Depth { get; } = children.Length == 0 ? 1 : children.Max(child => child.Depth) + 1;
        public bool IsConstant { get; } = kind != NodeKind.Variable && children.All(child => child.IsConstant);

        public void ValidateConstants()
        {
            foreach (var child in children) child.ValidateConstants();
            if (IsConstant) { _ = Evaluate(0); return; }
            if (children.Length < 2 || !children[1].IsConstant) return;
            var second = children[1].Evaluate(0);
            if (kind is NodeKind.Divide or NodeKind.Remainder && second == 0)
                throw new InvalidDataException("计算表达式的除数不能为零。");
            if (kind == NodeKind.Power) _ = IntegerParameter(second, -28, 28, "幂指数");
            if (kind == NodeKind.Round) _ = IntegerParameter(second, 0, 28, "round 小数位");
            if (kind == NodeKind.Clamp && children[2].IsConstant && second > children[2].Evaluate(0))
                throw new InvalidDataException("计算表达式 clamp 的下限不能高于上限。");
        }

        public decimal Evaluate(decimal x)
        {
            if (kind == NodeKind.Constant) return constant;
            if (kind == NodeKind.Variable) return x;
            var first = children[0].Evaluate(x);
            if (children.Length == 1)
                return kind switch
                {
                    NodeKind.Negate => checked(-first),
                    NodeKind.Abs => Math.Abs(first),
                    NodeKind.Round => Math.Round(first, 0, MidpointRounding.AwayFromZero),
                    NodeKind.Floor => Math.Floor(first),
                    NodeKind.Ceiling => Math.Ceiling(first),
                    NodeKind.Truncate => Math.Truncate(first),
                    _ => throw new InvalidDataException("计算表达式包含不支持的单参数运算。")
                };
            var second = children[1].Evaluate(x);
            return kind switch
            {
                NodeKind.Add => checked(first + second),
                NodeKind.Subtract => checked(first - second),
                NodeKind.Multiply => Multiply(first, second),
                NodeKind.Divide => Divide(first, second),
                NodeKind.Remainder => first % second,
                NodeKind.Power => Power(first, second),
                NodeKind.Minimum => Math.Min(first, second),
                NodeKind.Maximum => Math.Max(first, second),
                NodeKind.Round => Math.Round(first, IntegerParameter(second, 0, 28, "round 小数位"), MidpointRounding.AwayFromZero),
                NodeKind.Clamp => Clamp(first, second, children[2].Evaluate(x)),
                _ => throw new InvalidDataException("计算表达式包含不支持的运算。")
            };
        }

        private static decimal Clamp(decimal value, decimal minimum, decimal maximum)
        {
            if (minimum > maximum) throw new InvalidDataException("计算表达式 clamp 的下限不能高于上限。");
            return Math.Clamp(value, minimum, maximum);
        }

        private static int IntegerParameter(decimal value, int minimum, int maximum, string label)
        {
            if (decimal.Truncate(value) != value || value < minimum || value > maximum)
                throw new InvalidDataException($"计算表达式 {label} 必须为 {minimum}–{maximum} 的整数。");
            return (int)value;
        }

        private static decimal Power(decimal value, decimal exponent)
        {
            var count = IntegerParameter(exponent, -28, 28, "幂指数");
            if (count == 0) return 1;
            // A reciprocal first supports 1e20^-2 without overflowing an unused positive power.
            if (count < 0) { value = Divide(1, value); count = -count; }
            var result = 1m;
            for (var index = 0; index < count; index++) result = Multiply(result, value);
            return result;
        }

        private static decimal Multiply(decimal first, decimal second)
        {
            var result = checked(first * second);
            if (result == 0 && first != 0 && second != 0)
                throw new InvalidDataException("计算表达式结果低于 decimal 可表示的非零范围。");
            return result;
        }

        private static decimal Divide(decimal first, decimal second)
        {
            var result = first / second;
            if (result == 0 && first != 0)
                throw new InvalidDataException("计算表达式结果低于 decimal 可表示的非零范围。");
            return result;
        }
    }

    private sealed class Parser(string source)
    {
        private int _position;
        private int _nodes;
        private int _tokens;

        public Node Parse()
        {
            var result = ParseExpression(0, 1);
            SkipWhiteSpace();
            if (_position != source.Length) throw Error("存在无法识别的内容");
            return result;
        }

        private Node ParseExpression(int minimumBindingPower, int depth)
        {
            if (depth > MaximumDepth) throw Error($"嵌套不能超过 {MaximumDepth} 层");
            SkipWhiteSpace();
            if (_position == source.Length) throw Error("缺少数值、x 或左括号");
            CountToken();
            var current = source[_position];
            Node left;
            if (current is '+' or '-')
            {
                _position++;
                var operand = ParseExpression(30, depth + 1);
                left = current == '-' ? Create(NodeKind.Negate, operand) : operand;
            }
            else if (current == '(')
            {
                _position++;
                left = ParseExpression(0, depth + 1);
                Expect(')');
            }
            else if (char.IsAsciiDigit(current) || current == '.') left = ParseNumber();
            else if (char.IsAsciiLetter(current)) left = ParseIdentifier(depth);
            else throw Error("只支持数值、x、运算符和白名单函数");

            while (true)
            {
                SkipWhiteSpace();
                if (_position == source.Length) return left;
                var operation = source[_position];
                var (leftPower, rightPower, kind) = operation switch
                {
                    '+' => (10, 11, NodeKind.Add), '-' => (10, 11, NodeKind.Subtract),
                    '*' => (20, 21, NodeKind.Multiply), '/' => (20, 21, NodeKind.Divide),
                    '%' => (20, 21, NodeKind.Remainder), '^' => (40, 40, NodeKind.Power),
                    _ => (-1, -1, NodeKind.Constant)
                };
                if (leftPower < minimumBindingPower) return left;
                CountToken();
                _position++;
                var right = ParseExpression(rightPower, depth + 1);
                left = Create(kind, left, right);
            }
        }

        private Node ParseNumber()
        {
            var start = _position;
            while (_position < source.Length && char.IsAsciiDigit(source[_position])) _position++;
            if (_position < source.Length && source[_position] == '.')
            {
                _position++;
                while (_position < source.Length && char.IsAsciiDigit(source[_position])) _position++;
            }
            if (_position < source.Length && source[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < source.Length && source[_position] is '+' or '-') _position++;
                while (_position < source.Length && char.IsAsciiDigit(source[_position])) _position++;
            }
            var token = source.AsSpan(start, _position - start);
            if (token.Length > 64 || !decimal.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var value))
                throw Error("数值格式无效或超出 decimal 范围");
            if (value == 0 && HasNonZeroMantissa(token)) throw Error("非零数值低于 decimal 可表示范围");
            return CreateConstant(value);
        }

        private Node ParseIdentifier(int depth)
        {
            var start = _position;
            while (_position < source.Length && char.IsAsciiLetter(source[_position])) _position++;
            var identifier = source[start.._position].ToLowerInvariant();
            if (identifier == "x") return CountNode(Variable);
            var (kind, minimumArguments, maximumArguments) = identifier switch
            {
                "abs" => (NodeKind.Abs, 1, 1), "min" => (NodeKind.Minimum, 2, 2),
                "max" => (NodeKind.Maximum, 2, 2), "clamp" => (NodeKind.Clamp, 3, 3),
                "round" => (NodeKind.Round, 1, 2), "floor" => (NodeKind.Floor, 1, 1),
                "ceil" => (NodeKind.Ceiling, 1, 1), "truncate" => (NodeKind.Truncate, 1, 1),
                "pow" => (NodeKind.Power, 2, 2),
                _ => throw Error($"不支持标识符 {identifier}")
            };
            Expect('(');
            var arguments = new List<Node>(maximumArguments);
            while (true)
            {
                arguments.Add(ParseExpression(0, depth + 1));
                SkipWhiteSpace();
                if (_position >= source.Length || source[_position] != ',') break;
                if (arguments.Count >= maximumArguments) throw Error($"函数 {identifier} 参数过多");
                _position++;
                CountToken();
            }
            Expect(')');
            if (arguments.Count < minimumArguments) throw Error($"函数 {identifier} 缺少参数");
            return Create(kind, arguments.ToArray());
        }

        private Node CreateConstant(decimal value) => CountNode(new(NodeKind.Constant, value, []));
        private Node Create(NodeKind kind, params Node[] children) => CountNode(new(kind, 0, children));
        private Node CountNode(Node node)
        {
            if (++_nodes > MaximumNodes) throw Error($"计算节点不能超过 {MaximumNodes} 个");
            if (node.Depth > MaximumDepth) throw Error($"运算树不能超过 {MaximumDepth} 层");
            return node;
        }
        private void CountToken()
        {
            if (++_tokens > MaximumNodes * 2) throw Error("表达式过于复杂");
        }
        private void Expect(char expected)
        {
            SkipWhiteSpace();
            if (_position >= source.Length || source[_position] != expected) throw Error($"缺少 {expected}");
            _position++;
            CountToken();
        }
        private void SkipWhiteSpace()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++;
        }
        private InvalidDataException Error(string message) => new($"计算表达式第 {_position + 1} 列：{message}。");
    }
}

public sealed record ExpressionPreview(bool Success, decimal? Value, string? Error);
