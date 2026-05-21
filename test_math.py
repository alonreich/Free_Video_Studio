from fractions import Fraction

def test():
    current_bitrate_kbps = 25000
    ratio = Fraction(97, 100)
    MAX = 62500
    val = current_bitrate_kbps * ratio
    print(f"Val: {val}, type: {type(val)}")
    m = min(MAX, val)
    print(f"Min: {m}, type: {type(m)}")
    res = int(max(300, m))
    print(f"Res: {res}, type: {type(res)}")
if __name__ == "__main__":
    test()
