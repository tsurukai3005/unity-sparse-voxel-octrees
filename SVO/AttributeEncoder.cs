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

using UnityEngine;

namespace SVO
{
    /// <summary>
    /// ボクセル属性データの効率的エンコーディングクラス
    /// 
    /// メモリ最適化技術：
    /// 1. 法線ベクトルの32bit整数圧縮
    /// 2. Cube Mapping技術による情報損失最小化
    /// 3. GPU処理に最適化されたビット配置
    /// 4. 高速デコード対応のフォーマット設計
    /// </summary>
    public static class AttributeEncoder
    {
        /// <summary>
        /// 標準的なシェーディング属性をエンコード
        /// 
        /// 現在の実装では法線のみをサポート
        /// 将来的にはマテリアルプロパティ、ラフネス等の拡張可能
        /// </summary>
        /// <param name="normal">エンコード対象の法線ベクトル</param>
        /// <returns>エンコードされた属性データ配列</returns>
        public static int[] EncodeStandardAttributes(Vector3 normal)
        {
            return new[] { EncodeNormal(normal) };
        }
        
        /// <summary>
        /// 法線ベクトルを32bit整数に圧縮エンコード
        /// 
        /// Cube Mapping技術による高精度圧縮：
        /// 1. 立方体の6面への投影で主軸を特定
        /// 2. 主軸垂直な2成分のみを格納（第3成分は復元可能）
        /// 3. 10bit精度（1024レベル）での量子化
        /// 
        /// ビット配置（32bit）：
        /// - bit 31-23: 未使用（拡張用）
        /// - bit 22   : 主軸の符号（正=1, 負=0）
        /// - bit 21-20: 主軸インデックス（0=X, 1=Y, 2=Z）
        /// - bit 19-10: 第2成分（10bit）
        /// - bit 9-0  : 第3成分（10bit）
        /// </summary>
        /// <param name="normal">正規化された法線ベクトル</param>
        /// <returns>圧縮された32bit整数データ</returns>
        public static int EncodeNormal(Vector3 normal)
        {
            var encoded = 0;
            
            // 主軸の特定：最大絶対値成分を求める
            // Cube Mappingの基本原理：法線を立方体面に投影
            var maxAbsComp = Mathf.Max(Mathf.Max(Mathf.Abs(normal.x), Mathf.Abs(normal.y)), Mathf.Abs(normal.z));
            
            // 立方体座標系への変換：[-1,1] → [0,1]
            var cubicNormal = normal / maxAbsComp;
            var cubicNormalUnorm = cubicNormal * .5f + new Vector3(.5f, .5f, .5f);
            
            // X軸が主軸の場合
            if (Mathf.Abs(normal.x) == maxAbsComp)
            {
                // bit 22: X軸の符号エンコード
                encoded |= ((System.Math.Sign(normal.x) + 1) / 2) << 22;
                // bit 21-20: 軸インデックス = 0 (X軸)
                // bit 19-10: Y成分を10bitで量子化
                encoded |= (int)(1023f * cubicNormalUnorm.y) << 10;
                // bit 9-0: Z成分を10bitで量子化
                encoded |= (int)(1023f * cubicNormalUnorm.z);
            }
            // Y軸が主軸の場合
            else if (Mathf.Abs(normal.y) == maxAbsComp)
            {
                // bit 22: Y軸の符号エンコード
                encoded |= ((System.Math.Sign(normal.y) + 1) / 2) << 22;
                // bit 21-20: 軸インデックス = 1 (Y軸)
                encoded |= 1 << 20;
                // bit 19-10: X成分を10bitで量子化
                encoded |= (int)(1023f * cubicNormalUnorm.x) << 10;
                // bit 9-0: Z成分を10bitで量子化
                encoded |= (int)(1023f * cubicNormalUnorm.z);
            }
            // Z軸が主軸の場合
            else if (Mathf.Abs(normal.z) == maxAbsComp)
            {
                // bit 22: Z軸の符号エンコード
                encoded |= ((System.Math.Sign(normal.z) + 1) / 2) << 22;
                // bit 21-20: 軸インデックス = 2 (Z軸)
                encoded |= 2 << 20;
                // bit 19-10: X成分を10bitで量子化
                encoded |= (int)(1023f * cubicNormalUnorm.x) << 10;
                // bit 9-0: Y成分を10bitで量子化
                encoded |= (int)(1023f * cubicNormalUnorm.y);
            }

            return encoded;
        }
    }
}