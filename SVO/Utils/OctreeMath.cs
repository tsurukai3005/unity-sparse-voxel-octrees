/*
 *  Unity Sparse Voxel Octrees
 *  Copyright (C) 2021  Alexander Goslin
 *
 *  This program is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using UnityEngine;

namespace SVO
{
    /// <summary>
    /// オクトリー処理に特化した数学ユーティリティクラス
    /// 
    /// 主要機能：
    /// 1. 重心座標系による高精度補間
    /// 2. 3D空間での幾何学計算
    /// 3. 三角形面積計算
    /// 4. 数値微分（将来の拡張用）
    /// </summary>
    public static class OctreeMath
    {
        /// <summary>
        /// 3D空間における重心座標の計算
        /// 
        /// 重心座標系（Barycentric Coordinates）は三角形内の任意の点を
        /// 3つの頂点の重み付き平均として表現する座標系
        /// 
        /// アルゴリズム：
        /// 1. 点を三角形平面に投影
        /// 2. 投影点を使用して3つの部分三角形を構成
        /// 3. 各部分三角形の面積比が重心座標となる
        /// 
        /// 用途：
        /// - UV座標の補間
        /// - 法線ベクトルの補間
        /// - 属性データの高精度補間
        /// </summary>
        /// <param name="point">補間対象の3D点</param>
        /// <param name="p1">三角形の第1頂点</param>
        /// <param name="p2">三角形の第2頂点</param>
        /// <param name="p3">三角形の第3頂点</param>
        /// <returns>重心座標 (w1, w2, w3) ここで w1+w2+w3=1</returns>
        public static Vector3 ToBarycentricCoordinates(Vector3 point, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            // 三角形の法線ベクトルを計算（外積による面の向き決定）
            var norm = Vector3.Cross(p2 - p1, p3 - p1);
            
            // 点を三角形の平面に投影
            // 投影により、3D問題を2D問題に次元削減
            var pointProj = point - (Vector3.Dot(point - p1, norm) / norm.sqrMagnitude * norm);
            
            // 3つの部分三角形の面積を計算
            // 各面積は対応する重心座標の重みを表す
            
            // 面積1：点-p2-p3で構成される三角形（p1に対応する重み）
            var a1 = CalculateTriangleArea(pointProj, p2, p3);
            
            // 面積2：p1-点-p3で構成される三角形（p2に対応する重み）
            var a2 = CalculateTriangleArea(p1, pointProj, p3);
            
            // 面積3：p1-p2-点で構成される三角形（p3に対応する重み）
            var a3 = CalculateTriangleArea(p1, p2, pointProj);
            
            // 面積比による正規化：重心座標の定義により合計が1になる
            return new Vector3(a1, a2, a3) / (a1 + a2 + a3);
        }

        /// <summary>
        /// 3つの点で構成される三角形の面積計算
        /// 
        /// 外積を利用した高精度面積計算：
        /// 面積 = ||(p2-p1) × (p3-p1)|| / 2
        /// 
        /// 外積の幾何学的意味：
        /// - ベクトルの外積は平行四辺形の面積
        /// - 三角形面積はその半分
        /// - マグニチュードにより符号を除去
        /// </summary>
        /// <param name="p1">三角形の第1頂点</param>
        /// <param name="p2">三角形の第2頂点</param>
        /// <param name="p3">三角形の第3頂点</param>
        /// <returns>三角形の面積</returns>
        public static float CalculateTriangleArea(Vector3 p1, Vector3 p2, Vector3 p3)
        {
            // 外積による面積計算：ベクトル代数の基本公式を使用
            return Vector3.Cross(p2 - p1, p3 - p1).magnitude / 2;
        }

        /// <summary>
        /// 数値微分による関数の導関数推定
        /// 
        /// 前進差分法による近似：
        /// f'(x) ≈ (f(x+dx) - f(x)) / dx
        /// 
        /// 用途（将来の拡張）：
        /// - 距離場の勾配計算
        /// - アニメーション補間
        /// - 物理シミュレーション
        /// </summary>
        /// <param name="x">微分点</param>
        /// <param name="dx">微小変化量</param>
        /// <param name="f">対象関数</param>
        /// <returns>推定された導関数値</returns>
        public static float EstimateDerivative(float x, float dx, Func<float, float> f) 
            => (f(x + dx) - f(x)) / dx;
    }
}