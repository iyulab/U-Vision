namespace UVision.Edge.Agent.Rules;

/// <summary>
/// 규칙이 쓰는 기하 — 모두 프레임 기준 정규화 좌표(0~1, 왼쪽 위 원점, y 는 아래로 커진다). 순수 함수라 시계도 상태도 없다.
/// </summary>
public static class Geometry
{
    /// <summary>
    /// 점이 다각형 안인가 — 짝-홀 규칙(광선 교차). 꼭짓점 <c>[x, y]</c> 3개 이상. 변 위의 점은 어느 쪽으로 판정할지 정하지 않는다
    /// (규칙은 안정 구간으로 경계 떨림을 흡수하므로 경계 위의 한 점의 판정은 결과를 정하지 않는다).
    /// </summary>
    public static bool PointInPolygon(double x, double y, IReadOnlyList<IReadOnlyList<double>> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i][0], yi = polygon[i][1], xj = polygon[j][0], yj = polygon[j][1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// 점 <paramref name="p"/> 가 선 a→b 의 어느 쪽에 얼마나 떨어져 있나 — 부호 있는 거리(정규화 단위). 양수는 화면에서 a 에서 b 를
    /// 바라볼 때 <b>오른쪽</b>(y 가 아래로 커지는 화면 좌표의 외적 부호)이고 이쪽으로 넘어가는 것이 <c>in</c> 이다.
    /// </summary>
    public static double SignedDistance(double ax, double ay, double bx, double by, double px, double py)
    {
        var length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        return length == 0 ? 0 : ((bx - ax) * (py - ay) - (by - ay) * (px - ax)) / length;
    }

    /// <summary>
    /// 점 <paramref name="from"/>→<paramref name="to"/> 로 움직인 궤적이 선분 a-b <b>안에서</b> 선을 가로질렀나 — 두 점이 선의 서로
    /// 다른 쪽에 있어야 하고(같은 쪽 이동은 통과가 아니다), 교점이 선분의 두 끝점 사이여야 한다(선분 바깥에서 선의 연장을 넘은 것도 아니다).
    /// </summary>
    public static bool CrossesSegment(
        double ax, double ay, double bx, double by, double fromX, double fromY, double toX, double toY)
    {
        var d1 = SignedDistance(ax, ay, bx, by, fromX, fromY);
        var d2 = SignedDistance(ax, ay, bx, by, toX, toY);
        if (d1 == d2 || (d1 > 0) == (d2 > 0)) return false; // 같은 쪽(0 은 양쪽이 아닌 «선 위» 라 양이 아닌 쪽으로 친다)
        var t = d1 / (d1 - d2);
        var hitX = fromX + t * (toX - fromX);
        var hitY = fromY + t * (toY - fromY);
        var lengthSquared = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        if (lengthSquared == 0) return false;
        var s = ((hitX - ax) * (bx - ax) + (hitY - ay) * (by - ay)) / lengthSquared;
        return s is >= 0 and <= 1;
    }
}
