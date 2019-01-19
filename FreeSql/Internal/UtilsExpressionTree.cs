﻿using FreeSql.DataAnnotations;
using FreeSql.Internal.Model;
using Newtonsoft.Json.Linq;
using Npgsql.LegacyPostgis;
using NpgsqlTypes;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FreeSql.Internal {
	class Utils {

		static ConcurrentDictionary<string, ConcurrentDictionary<Type, TableInfo>> _cacheGetTableByEntity = new ConcurrentDictionary<string, ConcurrentDictionary<Type, TableInfo>>();
		internal static TableInfo GetTableByEntity(Type entity, CommonUtils common) {
			if (entity.FullName.StartsWith("<>f__AnonymousType")) return null;
			return _cacheGetTableByEntity.GetOrAdd(common.DbName, k1 => new ConcurrentDictionary<Type, TableInfo>()).GetOrAdd(entity, k2 => { //区分数据库类型缓存
				if (common.CodeFirst.GetDbInfo(entity) != null) return null;
				
				var tbattr = entity.GetCustomAttributes(typeof(TableAttribute), false).LastOrDefault() as TableAttribute;
				var trytb = new TableInfo();
				trytb.Type = entity;
				trytb.Properties = entity.GetProperties().ToDictionary(a => a.Name, a => a, StringComparer.CurrentCultureIgnoreCase);
				trytb.CsName = entity.Name;
				trytb.DbName = (tbattr?.Name ?? entity.Name);
				trytb.DbOldName = tbattr?.OldName;
				if (common.CodeFirst.IsSyncStructureToLower) {
					trytb.DbName = trytb.DbName.ToLower();
					trytb.DbOldName = trytb.DbOldName?.ToLower();
				}
				trytb.SelectFilter = tbattr?.SelectFilter;
				var virtualProps = new List<(PropertyInfo, bool, bool)>();
				foreach (var p in trytb.Properties.Values) {
					var tp = common.CodeFirst.GetDbInfo(p.PropertyType);
					//if (tp == null) continue;
					var colattr = p.GetCustomAttributes(typeof(ColumnAttribute), false).LastOrDefault() as ColumnAttribute;
					if (tp == null && colattr == null) {
						if (common.CodeFirst.IsLazyLoading) {
							var getIsVirtual = trytb.Type.GetMethod($"get_{p.Name}")?.IsVirtual;
							var setIsVirtual = trytb.Type.GetMethod($"set_{p.Name}")?.IsVirtual;
							if (getIsVirtual == true || setIsVirtual == true)
								virtualProps.Add((p, getIsVirtual == true, setIsVirtual == true));
						}
						continue;
					}
					if (colattr == null)
						colattr = new ColumnAttribute {
							Name = p.Name,
							DbType = tp.Value.dbtypeFull,
							IsIdentity = false,
							IsNullable = tp.Value.isnullable ?? true,
							IsPrimary = false,
						};
					if (string.IsNullOrEmpty(colattr.DbType)) colattr.DbType = tp?.dbtypeFull ?? "varchar(255)";
					colattr.DbType = colattr.DbType.ToUpper();

					if (tp != null && tp.Value.isnullable == null) colattr.IsNullable = tp.Value.dbtypeFull.Contains("NOT NULL") == false;
					if (colattr.DbType?.Contains("NOT NULL") == true) colattr.IsNullable = false;
					if (string.IsNullOrEmpty(colattr.Name)) colattr.Name = p.Name;
					if (common.CodeFirst.IsSyncStructureToLower) colattr.Name = colattr.Name.ToLower();

					if ((colattr.IsNullable == false || colattr.IsIdentity || colattr.IsPrimary) && colattr.DbType.Contains("NOT NULL") == false) {
						colattr.IsNullable = false;
						colattr.DbType += " NOT NULL";
					}
					if (colattr.IsNullable == true && colattr.DbType.Contains("NOT NULL")) colattr.DbType = colattr.DbType.Replace("NOT NULL", "");
					colattr.DbType = Regex.Replace(colattr.DbType, @"\([^\)]+\)", m => {
						var tmpLt = Regex.Replace(m.Groups[0].Value, @"\s", "");
						if (tmpLt.Contains("CHAR")) tmpLt = tmpLt.Replace("CHAR", " CHAR");
						if (tmpLt.Contains("BYTE")) tmpLt = tmpLt.Replace("BYTE", " BYTE");
						return tmpLt;
					});
					colattr.DbDefautValue = trytb.Properties[p.Name].GetValue(Activator.CreateInstance(trytb.Type));
					if (colattr.DbDefautValue == null) colattr.DbDefautValue = tp?.defaultValue;
					if (colattr.IsNullable == false && colattr.DbDefautValue == null) {
						var consturctorType = p.PropertyType.GenericTypeArguments.FirstOrDefault() ?? p.PropertyType;
						colattr.DbDefautValue = Activator.CreateInstance(consturctorType);
					}

					var col = new ColumnInfo {
						Table = trytb,
						CsName = p.Name,
						CsType = p.PropertyType,
						Attribute = colattr
					};
					trytb.Columns.Add(colattr.Name, col);
					trytb.ColumnsByCs.Add(p.Name, col);
				}
				trytb.Primarys = trytb.Columns.Values.Where(a => a.Attribute.IsPrimary).ToArray();
				if (trytb.Primarys.Any() == false) {
					trytb.Primarys = trytb.Columns.Values.Where(a => a.Attribute.IsIdentity).ToArray();
					foreach (var col in trytb.Primarys)
						col.Attribute.IsPrimary = true;
				}

				if (common.CodeFirst.IsLazyLoading && virtualProps.Any()) {
					//virtual 属性延时加载，生态产生新的重写类
					if (trytb.Type.IsNotPublic) throw new Exception("【延时加载】功能发生错误，实体类必须声明为 public");

					var overrieds = 0;
					var cscode = new StringBuilder();
					cscode.AppendLine("using System;")
						.AppendLine("using FreeSql.DataAnnotations;")
						.AppendLine("using System.Collections.Generic;")
						.AppendLine("using System.Linq;")
						.AppendLine("")
						.Append("public class FreeSqlOverrideLazyEntity").Append(trytb.Type.Name).Append(" : ").Append(trytb.Type.FullName.Replace("+", ".")).AppendLine(" {")
						.AppendLine("	public IFreeSql __fsql_orm__ { get; set; }\r\n");
					foreach(var vp in virtualProps) {
						TableInfo pktb = null;
						if (vp.Item1.PropertyType == trytb.Type) pktb = trytb;
						else pktb = GetTableByEntity(vp.Item1.PropertyType, common);
						if (pktb == null || pktb.Primarys.Any() == false) {
							//continue;
							throw new Exception($"【延时加载】功能发生错误，导航属性 {trytb.Type.FullName}.{vp.Item1.Name} 类型不正确，或者实体类型 {vp.Item1.PropertyType.FullName} 缺少主键标识");
						}

						var lmbdWhere = new StringBuilder();
						var vpcols = new ColumnInfo[pktb.Primarys.Length];
						for (var a = 0; a < pktb.Primarys.Length; a++) {
							if (trytb.ColumnsByCs.TryGetValue($"{vp.Item1.Name}{pktb.Primarys[a].CsName}", out var trycol) == false && //骆峰命名
								trytb.ColumnsByCs.TryGetValue($"{vp.Item1.Name}_{pktb.Primarys[a].CsName}", out trycol) == false //下划线命名
								) {
								pktb = null;
								throw new Exception($"【延时加载】功能发生错误，导航属性 {trytb.Type.FullName}.{vp.Item1.Name} 没有找到对应的字段 {vp.Item1.Name}{pktb.Primarys[a].CsName} 或 {vp.Item1.Name}_{pktb.Primarys[a].CsName}");
								//break;
							}
							if (a > 0) lmbdWhere.Append(" && ");
							lmbdWhere.Append("a.").Append(pktb.Primarys[a].CsName).Append(" == this.").Append(trycol.CsName);
						}
						if (pktb == null) continue;

						cscode.Append("	public override ").Append(vp.Item1.PropertyType.FullName.Replace("+", ".")).Append(" ").Append(vp.Item1.Name).AppendLine(" {");
						if (vp.Item2) { //get 重写
							cscode.Append("		get => base.").Append(vp.Item1.Name)
								.Append(" ?? (base.").Append(vp.Item1.Name)
								.Append(" = __fsql_orm__.Select<").Append(vp.Item1.PropertyType.FullName.Replace("+", ".")).Append(">().Where(a => ")
								.Append(lmbdWhere.ToString())
								.Append(").ToOne()").AppendLine(");");
						}
						if (vp.Item3) { //set 重写
							cscode.Append("		set => base.").Append(vp.Item1.Name).AppendLine(" = value;");
						}
						cscode.AppendLine("	}");
						++overrieds;
					}
					if (overrieds > 0) {
						cscode.AppendLine("}");
						var assemly = Generator.TemplateEngin._compiler.Value.CompileCode(cscode.ToString());
						var type = assemly.DefinedTypes.Where(a => a.FullName.EndsWith($"FreeSqlOverrideLazyEntity{trytb.Type.Name}")).FirstOrDefault();
						trytb.TypeLazy = type;
						trytb.TypeLazySetOrm = type.GetProperty("__fsql_orm__").GetSetMethod();
					}
				}
				return trytb;
			});
		}

		internal static T[] GetDbParamtersByObject<T>(string sql, object obj, string paramPrefix, Func<string, Type, object, T> constructorParamter) {
			if (string.IsNullOrEmpty(sql) || obj == null) return new T[0];
			var ttype = typeof(T);
			var type = obj.GetType();
			if (type == ttype) return new[] { (T)Convert.ChangeType(obj, type) };
			var ret = new List<T>();
			var ps = type.GetProperties();
			foreach (var p in ps) {
				if (sql.IndexOf($"{paramPrefix}{p.Name}", StringComparison.CurrentCultureIgnoreCase) == -1) continue;
				var pvalue = p.GetValue(obj);
				if (p.PropertyType == ttype) ret.Add((T)Convert.ChangeType(pvalue, ttype));
				else ret.Add(constructorParamter(p.Name, p.PropertyType, pvalue));
			}
			return ret.ToArray();
		}

		internal static Dictionary<Type, bool> dicExecuteArrayRowReadClassOrTuple = new Dictionary<Type, bool> {
			[typeof(bool)] = true,
			[typeof(sbyte)] = true,
			[typeof(short)] = true,
			[typeof(int)] = true,
			[typeof(long)] = true,
			[typeof(byte)] = true,
			[typeof(ushort)] = true,
			[typeof(uint)] = true,
			[typeof(ulong)] = true,
			[typeof(double)] = true,
			[typeof(float)] = true,
			[typeof(decimal)] = true,
			[typeof(TimeSpan)] = true,
			[typeof(DateTime)] = true,
			[typeof(DateTimeOffset)] = true,
			[typeof(byte[])] = true,
			[typeof(string)] = true,
			[typeof(Guid)] = true,
			[typeof(MygisPoint)] = true,
			[typeof(MygisLineString)] = true,
			[typeof(MygisPolygon)] = true,
			[typeof(MygisMultiPoint)] = true,
			[typeof(MygisMultiLineString)] = true,
			[typeof(MygisMultiPolygon)] = true,
			[typeof(BitArray)] = true,
			[typeof(NpgsqlPoint)] = true,
			[typeof(NpgsqlLine)] = true,
			[typeof(NpgsqlLSeg)] = true,
			[typeof(NpgsqlBox)] = true,
			[typeof(NpgsqlPath)] = true,
			[typeof(NpgsqlPolygon)] = true,
			[typeof(NpgsqlCircle)] = true,
			[typeof((IPAddress Address, int Subnet))] = true,
			[typeof(IPAddress)] = true,
			[typeof(PhysicalAddress)] = true,
			[typeof(NpgsqlRange<int>)] = true,
			[typeof(NpgsqlRange<long>)] = true,
			[typeof(NpgsqlRange<decimal>)] = true,
			[typeof(NpgsqlRange<DateTime>)] = true,
			[typeof(PostgisPoint)] = true,
			[typeof(PostgisLineString)] = true,
			[typeof(PostgisPolygon)] = true,
			[typeof(PostgisMultiPoint)] = true,
			[typeof(PostgisMultiLineString)] = true,
			[typeof(PostgisMultiPolygon)] = true,
			[typeof(PostgisGeometry)] = true,
			[typeof(PostgisGeometryCollection)] = true,
			[typeof(Dictionary<string, string>)] = true,
			[typeof(JToken)] = true,
			[typeof(JObject)] = true,
			[typeof(JArray)] = true,
		};
		internal static ConcurrentDictionary<Type, Func<Type, int[], DbDataReader, int, RowInfo>> _dicExecuteArrayRowReadClassOrTuple = new ConcurrentDictionary<Type, Func<Type, int[], DbDataReader, int, RowInfo>>();
		internal class RowInfo {
			public object Value { get; set; }
			public int DataIndex { get; set; }
			public RowInfo(object value, int dataIndex) {
				this.Value = value;
				this.DataIndex = dataIndex;
			}
			public static ConstructorInfo Constructor = typeof(RowInfo).GetConstructor(new[] { typeof(object), typeof(int) });
			public static PropertyInfo PropertyValue = typeof(RowInfo).GetProperty("Value");
			public static PropertyInfo PropertyDataIndex = typeof(RowInfo).GetProperty("DataIndex");
		}
		internal static MethodInfo MethodDataReaderGetValue = typeof(DbDataReader).GetMethod("GetValue");
		internal static RowInfo ExecuteArrayRowReadClassOrTuple(Type type, int[] indexes, DbDataReader row, int dataIndex = 0) {
			var func = _dicExecuteArrayRowReadClassOrTuple.GetOrAdd(type, s => {
				var returnTarget = Expression.Label(typeof(RowInfo));
				var typeExp = Expression.Parameter(typeof(Type), "type");
				var indexesExp = Expression.Parameter(typeof(int[]), "indexes");
				var rowExp = Expression.Parameter(typeof(DbDataReader), "row");
				var dataIndexExp = Expression.Parameter(typeof(int), "dataIndex");

				if (type.IsArray) return Expression.Lambda<Func<Type, int[], DbDataReader, int, RowInfo>>(
					Expression.New(RowInfo.Constructor,
						GetDataReaderValueBlockExpression(type, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
						//Expression.Call(MethodGetDataReaderValue, new Expression[] { typeExp, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
						Expression.Add(dataIndexExp, Expression.Constant(1))
					), new[] { typeExp, indexesExp, rowExp, dataIndexExp }).Compile();

				var typeGeneric = type;
				if (typeGeneric.FullName.StartsWith("System.Nullable`1[")) typeGeneric = type.GenericTypeArguments.First();
				if (typeGeneric.IsEnum ||
					dicExecuteArrayRowReadClassOrTuple.ContainsKey(typeGeneric))
					return Expression.Lambda<Func<Type, int[], DbDataReader, int, RowInfo>>(
					Expression.New(RowInfo.Constructor,
						GetDataReaderValueBlockExpression(type, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
						//Expression.Call(MethodGetDataReaderValue, new Expression[] { typeExp, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
						Expression.Add(dataIndexExp, Expression.Constant(1))
					), new[] { typeExp, indexesExp, rowExp, dataIndexExp }).Compile();

				if (type.Namespace == "System" && (type.FullName == "System.String" || type.IsValueType)) { //值类型，或者元组
					bool isTuple = type.Name.StartsWith("ValueTuple`");
					if (isTuple) {
						var ret2Exp = Expression.Variable(type, "ret");
						var read2Exp = Expression.Variable(typeof(RowInfo), "read");
						var read2ExpValue = Expression.MakeMemberAccess(read2Exp, RowInfo.PropertyValue);
						var read2ExpDataIndex = Expression.MakeMemberAccess(read2Exp, RowInfo.PropertyDataIndex);
						var block2Exp = new List<Expression>();

						var fields = type.GetFields();
						foreach (var field in fields) {
							Expression read2ExpAssign = null; //加速缓存
							if (field.FieldType.IsArray) read2ExpAssign = Expression.New(RowInfo.Constructor,
								GetDataReaderValueBlockExpression(field.FieldType, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
								//Expression.Call(MethodGetDataReaderValue, new Expression[] { Expression.Constant(field.FieldType), Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
								Expression.Add(dataIndexExp, Expression.Constant(1))
							);
							else {
								var fieldtypeGeneric = field.FieldType;
								if (fieldtypeGeneric.FullName.StartsWith("System.Nullable`1[")) fieldtypeGeneric = fieldtypeGeneric.GenericTypeArguments.First();
								if (fieldtypeGeneric.IsEnum ||
									dicExecuteArrayRowReadClassOrTuple.ContainsKey(fieldtypeGeneric)) read2ExpAssign = Expression.New(RowInfo.Constructor,
										GetDataReaderValueBlockExpression(field.FieldType, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
										//Expression.Call(MethodGetDataReaderValue, new Expression[] { Expression.Constant(field.FieldType), Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
										Expression.Add(dataIndexExp, Expression.Constant(1))
								);
								else {
									read2ExpAssign = Expression.Call(MethodExecuteArrayRowReadClassOrTuple, new Expression[] { Expression.Constant(field.FieldType), indexesExp, rowExp, dataIndexExp });
								}
							}
							block2Exp.AddRange(new Expression[] {
								//Expression.TryCatch(Expression.Block(
								//	typeof(void),
									Expression.Assign(read2Exp, read2ExpAssign),
									Expression.IfThen(Expression.GreaterThan(read2ExpDataIndex, dataIndexExp),
										Expression.Assign(dataIndexExp, read2ExpDataIndex)),
									Expression.IfThenElse(Expression.Equal(read2ExpValue, Expression.Constant(null)),
										Expression.Assign(Expression.MakeMemberAccess(ret2Exp, field), Expression.Default(field.FieldType)),
										Expression.Assign(Expression.MakeMemberAccess(ret2Exp, field), Expression.Convert(read2ExpValue, field.FieldType)))
								//), 
								//Expression.Catch(typeof(Exception), Expression.Block(
								//		Expression.IfThen(Expression.Equal(read2ExpDataIndex, Expression.Constant(0)), Expression.Throw(Expression.Constant(new Exception(field.Name + "," + 0)))),
								//		Expression.IfThen(Expression.Equal(read2ExpDataIndex, Expression.Constant(1)), Expression.Throw(Expression.Constant(new Exception(field.Name + "," + 1)))),
								//		Expression.IfThen(Expression.Equal(read2ExpDataIndex, Expression.Constant(2)), Expression.Throw(Expression.Constant(new Exception(field.Name + "," + 2)))),
								//		Expression.IfThen(Expression.Equal(read2ExpDataIndex, Expression.Constant(3)), Expression.Throw(Expression.Constant(new Exception(field.Name + "," + 3)))),
								//		Expression.IfThen(Expression.Equal(read2ExpDataIndex, Expression.Constant(4)), Expression.Throw(Expression.Constant(new Exception(field.Name + "," + 4))))
								//	)
								//))
							});
						}
						block2Exp.AddRange(new Expression[] {
							Expression.Return(returnTarget, Expression.New(RowInfo.Constructor, Expression.Convert(ret2Exp, typeof(object)), dataIndexExp)),
							Expression.Label(returnTarget, Expression.Default(typeof(RowInfo)))
						});
						return Expression.Lambda<Func<Type, int[], DbDataReader, int, RowInfo>>(
							Expression.Block(new[] { ret2Exp, read2Exp }, block2Exp), new[] { typeExp, indexesExp, rowExp, dataIndexExp }).Compile();
					}
					var rowLenExp = Expression.ArrayLength(rowExp);
					return Expression.Lambda<Func<Type, int[], DbDataReader, int, RowInfo>>(
						Expression.Block(
							Expression.IfThen(
								Expression.LessThan(dataIndexExp, rowLenExp),
									Expression.Return(returnTarget, Expression.New(RowInfo.Constructor,
										GetDataReaderValueBlockExpression(type, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
										//Expression.Call(MethodGetDataReaderValue, new Expression[] { typeExp, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
										Expression.Add(dataIndexExp, Expression.Constant(1))))
							),
							Expression.Label(returnTarget, Expression.Default(typeof(RowInfo)))
						), new[] { typeExp, indexesExp, rowExp, dataIndexExp }).Compile();
				}

				if (type == typeof(object) && indexes != null) {
					Func<Type, int[], DbDataReader, int, RowInfo> dynamicFunc = (type2, indexes2, row2, dataindex2) => {
						dynamic expando = new System.Dynamic.ExpandoObject(); //动态类型字段 可读可写
						var expandodic = (IDictionary<string, object>)expando;
						var fc = row2.FieldCount;
						for (var a = 0; a < fc; a++)
							expandodic.Add(row2.GetName(a), row2.GetValue(a));
						return new RowInfo(expando, fc);
					};
					return dynamicFunc;// Expression.Lambda<Func<Type, int[], DbDataReader, int, RowInfo>>(null);
				}

				//类注入属性
				var retExp = Expression.Variable(type, "ret");
				var readExp = Expression.Variable(typeof(RowInfo), "read");
				var readExpValue = Expression.MakeMemberAccess(readExp, RowInfo.PropertyValue);
				var readExpDataIndex = Expression.MakeMemberAccess(readExp, RowInfo.PropertyDataIndex);
				var readExpValueParms = new List<ParameterExpression>();
				var readExpsIndex = Expression.Variable(typeof(int), "readsIndex");
				var tryidxExp = Expression.Variable(typeof(int), "tryidx");
				var indexesLengthExp = Expression.Parameter(typeof(int), "indexesLength");
				var blockExp = new List<Expression>();
				var ctor = type.GetConstructor(new Type[0]) ?? type.GetConstructors().First();
				var ctorParms = ctor.GetParameters();
				if (ctorParms.Length > 0) {
					foreach (var ctorParm in ctorParms) {
						Expression readExpAssign = null; //加速缓存
						if (ctorParm.ParameterType.IsArray) readExpAssign = Expression.New(RowInfo.Constructor,
							GetDataReaderValueBlockExpression(ctorParm.ParameterType, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
							//Expression.Call(MethodGetDataReaderValue, new Expression[] { Expression.Constant(ctorParm.ParameterType), Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
							Expression.Add(dataIndexExp, Expression.Constant(1))
						);
						else {
							var proptypeGeneric = ctorParm.ParameterType;
							if (proptypeGeneric.FullName.StartsWith("System.Nullable`1[")) proptypeGeneric = proptypeGeneric.GenericTypeArguments.First();
							if (proptypeGeneric.IsEnum ||
								dicExecuteArrayRowReadClassOrTuple.ContainsKey(proptypeGeneric)) readExpAssign = Expression.New(RowInfo.Constructor,
									GetDataReaderValueBlockExpression(ctorParm.ParameterType, Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp)),
									//Expression.Call(MethodGetDataReaderValue, new Expression[] { Expression.Constant(ctorParm.ParameterType), Expression.Call(rowExp, MethodDataReaderGetValue, dataIndexExp) }),
									Expression.Add(dataIndexExp, Expression.Constant(1))
							);
							else {
								readExpAssign = Expression.New(RowInfo.Constructor,
									Expression.MakeMemberAccess(Expression.Call(MethodExecuteArrayRowReadClassOrTuple, new Expression[] { Expression.Constant(ctorParm.ParameterType), indexesExp, rowExp, dataIndexExp }), RowInfo.PropertyValue),
									Expression.Add(dataIndexExp, Expression.Constant(1)));
							}
						}
						var varctorParm = Expression.Variable(ctorParm.ParameterType, $"ctorParm{ctorParm.Name}");
						readExpValueParms.Add(varctorParm);
						blockExp.AddRange(new Expression[] {
							Expression.Assign(tryidxExp, dataIndexExp),
							Expression.Assign(readExp, readExpAssign),
							Expression.IfThen(Expression.GreaterThan(readExpDataIndex, dataIndexExp),
								Expression.Assign(dataIndexExp, readExpDataIndex)),
							Expression.IfThenElse(Expression.Equal(readExpValue, Expression.Constant(null)),
								Expression.Assign(varctorParm, Expression.Default(ctorParm.ParameterType)),
								Expression.Assign(varctorParm, Expression.Convert(readExpValue, ctorParm.ParameterType)))
						});
					}
					blockExp.Add(Expression.Assign(retExp, Expression.New(ctor, readExpValueParms)));
				} else {
					blockExp.AddRange(new Expression[] {
						Expression.Assign(retExp, Expression.New(ctor)),
						Expression.Assign(indexesLengthExp, Expression.Constant(0)),
						Expression.IfThen(
							Expression.NotEqual(indexesExp, Expression.Constant(null)),
							Expression.Assign(indexesLengthExp, Expression.ArrayLength(indexesExp))
						)
					});
					
					var props = type.GetProperties();//.ToDictionary(a => a.Name, a => a, StringComparer.CurrentCultureIgnoreCase);
					var propIndex = 0;
					foreach (var prop in props) {
						var propGetSetMethod = prop.GetSetMethod();
						Expression readExpAssign = null; //加速缓存
						if (prop.PropertyType.IsArray) readExpAssign = Expression.New(RowInfo.Constructor,
							GetDataReaderValueBlockExpression(prop.PropertyType, Expression.Call(rowExp, MethodDataReaderGetValue, tryidxExp)),
							//Expression.Call(MethodGetDataReaderValue, new Expression[] { Expression.Constant(prop.PropertyType), Expression.Call(rowExp, MethodDataReaderGetValue, tryidxExp) }),
							Expression.Add(tryidxExp, Expression.Constant(1))
						);
						else {
							var proptypeGeneric = prop.PropertyType;
							if (proptypeGeneric.FullName.StartsWith("System.Nullable`1[")) proptypeGeneric = proptypeGeneric.GenericTypeArguments.First();
							if (proptypeGeneric.IsEnum ||
								dicExecuteArrayRowReadClassOrTuple.ContainsKey(proptypeGeneric)) readExpAssign = Expression.New(RowInfo.Constructor,
									GetDataReaderValueBlockExpression(prop.PropertyType, Expression.Call(rowExp, MethodDataReaderGetValue, tryidxExp)),
									//Expression.Call(MethodGetDataReaderValue, new Expression[] { Expression.Constant(prop.PropertyType), Expression.Call(rowExp, MethodDataReaderGetValue, tryidxExp) }),
									Expression.Add(tryidxExp, Expression.Constant(1))
							);
							else {
								continue;
								//readExpAssign = Expression.Call(MethodExecuteArrayRowReadClassOrTuple, new Expression[] { Expression.Constant(prop.PropertyType), indexesExp, rowExp, tryidxExp });
							}
						}
						blockExp.AddRange(new Expression[] {
							//以下注释部分为【严格读取】，会损失一点性能，使用 select * from xxx 与属性映射赋值
							Expression.IfThenElse(
								Expression.LessThan(Expression.Constant(propIndex), indexesLengthExp),
								Expression.Assign(tryidxExp, Expression.ArrayAccess(indexesExp, Expression.Constant(propIndex))),
								Expression.Assign(tryidxExp, dataIndexExp)
							),
							Expression.Assign(readExp, readExpAssign),
							Expression.IfThen(Expression.GreaterThan(readExpDataIndex, dataIndexExp),
								Expression.Assign(dataIndexExp, readExpDataIndex)),
							Expression.IfThenElse(Expression.Equal(readExpValue, Expression.Constant(null)),
								Expression.Call(retExp, propGetSetMethod, Expression.Default(prop.PropertyType)),
								Expression.Call(retExp, propGetSetMethod, Expression.Convert(readExpValue, prop.PropertyType)))
						});
						++propIndex;
					}
				}
				blockExp.AddRange(new Expression[] {
					Expression.Return(returnTarget, Expression.New(RowInfo.Constructor, retExp, dataIndexExp)),
					Expression.Label(returnTarget, Expression.Default(typeof(RowInfo)))
				});
				return Expression.Lambda<Func<Type, int[], DbDataReader, int, RowInfo>>(
					Expression.Block(new[] { retExp, readExp, tryidxExp, readExpsIndex, indexesLengthExp }.Concat(readExpValueParms), blockExp), new[] { typeExp, indexesExp, rowExp, dataIndexExp }).Compile();
			});
			return func(type, indexes, row, dataIndex);
		}

		internal static MethodInfo MethodExecuteArrayRowReadClassOrTuple = typeof(Utils).GetMethod("ExecuteArrayRowReadClassOrTuple", BindingFlags.Static | BindingFlags.NonPublic);
		internal static MethodInfo MethodGetDataReaderValue = typeof(Utils).GetMethod("GetDataReaderValue", BindingFlags.Static | BindingFlags.NonPublic);

		static ConcurrentDictionary<string, Action<object, object>> _dicFillPropertyValue = new ConcurrentDictionary<string, Action<object, object>>();
		internal static void FillPropertyValue(object info, string memberAccessPath, object value) {
			var typeObj = info.GetType();
			var typeValue = value.GetType();
			var key = "FillPropertyValue_" + typeObj.FullName + "_" + typeValue.FullName;
			var act = _dicFillPropertyValue.GetOrAdd($"{key}.{memberAccessPath}", s => {
				var parmInfo = Expression.Parameter(typeof(object), "info");
				var parmValue = Expression.Parameter(typeof(object), "value");
				Expression exp = Expression.Convert(parmInfo, typeObj);
				foreach (var pro in memberAccessPath.Split('.'))
					exp = Expression.PropertyOrField(exp, pro) ?? throw new Exception(string.Concat(exp.Type.FullName, " 没有定义属性 ", pro));

				var value2 = Expression.Call(MethodGetDataReaderValue, Expression.Constant(exp.Type), parmValue);
				var value3 = Expression.Convert(parmValue, typeValue);
				exp = Expression.Assign(exp, value3);
				return Expression.Lambda<Action<object, object>>(exp, parmInfo, parmValue).Compile();
			});
			act(info, value);
		}

		static ConcurrentDictionary<Type, ConcurrentDictionary<Type, Func<object, object>>> _dicGetDataReaderValue = new ConcurrentDictionary<Type, ConcurrentDictionary<Type, Func<object, object>>>();
		static MethodInfo MethodArrayGetValue = typeof(Array).GetMethod("GetValue", new[] { typeof(int) });
		static MethodInfo MethodArrayGetLength = typeof(Array).GetMethod("GetLength", new[] { typeof(int) });
		static MethodInfo MethodMygisGeometryParse = typeof(MygisGeometry).GetMethod("Parse", new[] { typeof(string) });
		static MethodInfo MethodGuidParse = typeof(Guid).GetMethod("Parse", new[] { typeof(string) });
		static MethodInfo MethodEnumParse = typeof(Enum).GetMethod("Parse", new[] { typeof(Type), typeof(string), typeof(bool) });
		static MethodInfo MethodToString = typeof(string).GetMethod("Concat", new[] { typeof(object) });
		static MethodInfo MethodConvertChangeType = typeof(Convert).GetMethod("ChangeType", new[] { typeof(object), typeof(Type) });
		static MethodInfo MethodTimeSpanFromSeconds = typeof(TimeSpan).GetMethod("FromSeconds");
		static MethodInfo MethodDoubleParse = typeof(double).GetMethod("Parse", new[] { typeof(string) });
		static MethodInfo MethodJTokenParse = typeof(JToken).GetMethod("Parse", new[] { typeof(string) });
		static MethodInfo MethodJObjectParse = typeof(JObject).GetMethod("Parse", new[] { typeof(string) });
		static MethodInfo MethodJArrayParse = typeof(JArray).GetMethod("Parse", new[] { typeof(string) });
		internal static Expression GetDataReaderValueBlockExpression(Type type, Expression value) {
			var returnTarget = Expression.Label(typeof(object));
			var valueExp = Expression.Variable(typeof(object), "locvalue");
			Func<Expression> funcGetExpression = () => {
				if (type.FullName == "System.Byte[]") return Expression.Return(returnTarget, valueExp);
				if (type.IsArray) {
					var elementType = type.GetElementType();
					var arrNewExp = Expression.Variable(type, "arrNew");
					var arrExp = Expression.Variable(typeof(Array), "arr");
					var arrLenExp = Expression.Variable(typeof(int), "arrLen");
					var arrXExp = Expression.Variable(typeof(int), "arrX");
					var arrReadValExp = Expression.Variable(typeof(object), "arrReadVal");
					var label = Expression.Label(typeof(int));
					return Expression.IfThenElse(
						Expression.TypeEqual(valueExp, type),
						Expression.Return(returnTarget, valueExp), 
						Expression.Block(
							new[] { arrNewExp, arrExp, arrLenExp, arrXExp, arrReadValExp },
							Expression.Assign(arrExp, Expression.TypeAs(valueExp, typeof(Array))),
							Expression.Assign(arrLenExp, Expression.Call(arrExp, MethodArrayGetLength, Expression.Constant(0))),
							Expression.Assign(arrXExp, Expression.Constant(0)),
							Expression.Assign(arrNewExp, Expression.NewArrayBounds(elementType, arrLenExp)),
							Expression.Loop(
								Expression.IfThenElse(
									Expression.LessThan(arrXExp, arrLenExp),
									Expression.Block(
										Expression.Assign(arrReadValExp, GetDataReaderValueBlockExpression(elementType, Expression.Call(arrExp, MethodArrayGetValue, arrXExp))),
										Expression.IfThenElse(
											Expression.Equal(arrReadValExp, Expression.Constant(null)),
											Expression.Assign(Expression.ArrayAccess(arrNewExp, arrXExp), Expression.Default(elementType)),
											Expression.Assign(Expression.ArrayAccess(arrNewExp, arrXExp), Expression.Convert(arrReadValExp, elementType))
										),
										Expression.PostIncrementAssign(arrXExp)
									),
									Expression.Break(label, arrXExp)
								),
								label
							),
							Expression.Return(returnTarget, arrNewExp)
						)
					);
				}
				if (type.FullName.StartsWith("System.Nullable`1[")) type = type.GenericTypeArguments.First();
				if (type.IsEnum) return Expression.Return(returnTarget, Expression.Call(MethodEnumParse, Expression.Constant(type, typeof(Type)), Expression.Call(MethodToString, valueExp), Expression.Constant(true, typeof(bool))));
				switch(type.FullName) {
					case "System.Guid": return Expression.IfThenElse(
							Expression.TypeEqual(valueExp, type),
							Expression.Return(returnTarget, valueExp),
							Expression.Return(returnTarget, Expression.Convert(Expression.Call(MethodGuidParse, Expression.Convert(valueExp, typeof(string))), typeof(object)))
						);
					case "MygisPoint": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodMygisGeometryParse, Expression.Convert(valueExp, typeof(string))), typeof(MygisPoint)));
					case "MygisLineString": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodMygisGeometryParse, Expression.Convert(valueExp, typeof(string))), typeof(MygisLineString)));
					case "MygisPolygon": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodMygisGeometryParse, Expression.Convert(valueExp, typeof(string))), typeof(MygisPolygon)));
					case "MygisMultiPoint": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodMygisGeometryParse, Expression.Convert(valueExp, typeof(string))), typeof(MygisMultiPoint)));
					case "MygisMultiLineString": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodMygisGeometryParse, Expression.Convert(valueExp, typeof(string))), typeof(MygisMultiLineString)));
					case "MygisMultiPolygon": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodMygisGeometryParse, Expression.Convert(valueExp, typeof(string))), typeof(MygisMultiPolygon)));
					case "Newtonsoft.Json.Linq.JToken": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodJTokenParse, Expression.Convert(valueExp, typeof(string))), typeof(JToken)));
					case "Newtonsoft.Json.Linq.JObject": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodJObjectParse, Expression.Convert(valueExp, typeof(string))), typeof(JObject)));
					case "Newtonsoft.Json.Linq.JArray": return Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(MethodJArrayParse, Expression.Convert(valueExp, typeof(string))), typeof(JArray)));
					case "Npgsql.LegacyPostgis.PostgisGeometry": return Expression.Return(returnTarget, valueExp);
					case "System.TimeSpan": return Expression.IfThenElse(
							Expression.TypeEqual(valueExp, type),
							Expression.Return(returnTarget, valueExp),
							Expression.Return(returnTarget, Expression.Convert(Expression.Call(MethodTimeSpanFromSeconds, Expression.Call(MethodDoubleParse, Expression.Call(MethodToString, valueExp))), typeof(object)))
						);
				}
				return Expression.IfThenElse(
					Expression.TypeEqual(valueExp, type),
					Expression.Return(returnTarget, valueExp),
					Expression.Return(returnTarget, Expression.Call(MethodConvertChangeType, valueExp, Expression.Constant(type, typeof(Type))))
				);
			};

			return Expression.Block(
				new[] { valueExp },
				Expression.Assign(valueExp, value),
				Expression.IfThenElse(
					Expression.Or(
						Expression.Equal(valueExp, Expression.Constant(null)),
						Expression.Equal(valueExp, Expression.Constant(DBNull.Value))
					),
					Expression.Return(returnTarget, Expression.Convert(Expression.Default(type), typeof(object))),
					funcGetExpression()
				), 
				Expression.Label(returnTarget, Expression.Default(typeof(object)))
			);
		}
		internal static object GetDataReaderValue(Type type, object value) {
			if (value == null || value == DBNull.Value) return null;
			var func = _dicGetDataReaderValue.GetOrAdd(type, k1 => new ConcurrentDictionary<Type, Func<object, object>>()).GetOrAdd(value.GetType(), valueType => {
				var parmExp = Expression.Parameter(typeof(object), "value");
				var exp = GetDataReaderValueBlockExpression(type, parmExp);
				return Expression.Lambda<Func<object, object>>(exp, parmExp).Compile();
			});
			return func(value);

			//var func = _dicGetDataReaderValue.GetOrAdd(type, k1 => new ConcurrentDictionary<Type, Func<object, object>>()).GetOrAdd(value.GetType(), valueType => {
			//	var returnTarget = Expression.Label(typeof(object));
			//	var parmExp = Expression.Parameter(typeof(object), "value");

			//	if (type.FullName == "System.Byte[]") return Expression.Lambda<Func<object, object>>(parmExp, parmExp).Compile();

			//	if (type.IsArray) {
			//		var elementType = type.GetElementType();
			//		if (elementType == valueType.GetElementType()) return Expression.Lambda<Func<object, object>>(parmExp, parmExp).Compile();

			//		var ret = Expression.Variable(type, "ret");
			//		var arr = Expression.Variable(valueType, "arr");
			//		var arrlen = Expression.Variable(typeof(int), "arrlen");
			//		var x = Expression.Variable(typeof(int), "x");
			//		var readval = Expression.Variable(typeof(object), "readval");
			//		var label = Expression.Label(typeof(int));
			//		return Expression.Lambda<Func<object, object>>(
			//			Expression.Block(
			//				new[] { ret, arr, arrlen, readval, x },
			//				Expression.Assign(arr, Expression.TypeAs(parmExp, valueType)),
			//				Expression.Assign(arrlen, Expression.ArrayLength(arr)),
			//				Expression.Assign(x, Expression.Constant(0)),
			//				Expression.Assign(ret, Expression.NewArrayBounds(elementType, arrlen)),
			//				Expression.Loop(
			//					Expression.IfThenElse(
			//						Expression.LessThan(x, arrlen),
			//						Expression.Block(
			//							Expression.Assign(readval, Expression.Call(
			//								MethodGetDataReaderValue,
			//								Expression.Constant(elementType, typeof(Type)),
			//								Expression.Convert(Expression.ArrayAccess(arr, x), typeof(object))
			//							)),
			//							Expression.IfThenElse(
			//								Expression.Equal(readval, Expression.Constant(null)),
			//								Expression.Assign(Expression.ArrayAccess(ret, x), Expression.Default(elementType)),
			//								Expression.Assign(Expression.ArrayAccess(ret, x), Expression.Convert(readval, elementType))
			//							),
			//							Expression.PostIncrementAssign(x)
			//						),
			//						Expression.Break(label, x)
			//					),
			//					label
			//				),
			//				Expression.Return(returnTarget, ret),
			//				Expression.Label(returnTarget, Expression.Default(typeof(object)))
			//			), parmExp).Compile();
			//	}

			//	if (type.FullName.StartsWith("System.Nullable`1[")) type = type.GenericTypeArguments.First();
			//	if (type.IsEnum) return Expression.Lambda<Func<object, object>>(
			//		Expression.Call(
			//			MethodEnumParse,
			//			Expression.Constant(type, typeof(Type)),
			//			Expression.Call(MethodToString, parmExp),
			//			Expression.Constant(true, typeof(bool))
			//		) , parmExp).Compile();

			//	switch (type.FullName) {
			//		case "System.Guid":
			//			if (valueType != type) return Expression.Lambda<Func<object, object>>(
			//				Expression.Convert(Expression.Call(MethodGuidParse, Expression.Convert(parmExp, typeof(string))), typeof(object))
			//				, parmExp).Compile();
			//			return Expression.Lambda<Func<object, object>>(parmExp, parmExp).Compile();

			//		case "MygisPoint": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(MethodMygisGeometryParse, Expression.Convert(parmExp, typeof(string))),
			//					typeof(MygisPoint)
			//				), parmExp).Compile();
			//		case "MygisLineString": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(MethodMygisGeometryParse, Expression.Convert(parmExp, typeof(string))), 
			//					typeof(MygisLineString)
			//				), parmExp).Compile();
			//		case "MygisPolygon": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(MethodMygisGeometryParse, Expression.Convert(parmExp, typeof(string))), 
			//					typeof(MygisPolygon)
			//				), parmExp).Compile();
			//		case "MygisMultiPoint": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(MethodMygisGeometryParse, Expression.Convert(parmExp, typeof(string))), 
			//					typeof(MygisMultiPoint)
			//				), parmExp).Compile();
			//		case "MygisMultiLineString": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(MethodMygisGeometryParse, Expression.Convert(parmExp, typeof(string))), 
			//					typeof(MygisMultiLineString)
			//				), parmExp).Compile();
			//		case "MygisMultiPolygon": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(MethodMygisGeometryParse, Expression.Convert(parmExp, typeof(string))), 
			//					typeof(MygisMultiPolygon)
			//				), parmExp).Compile();
			//		case "Newtonsoft.Json.Linq.JToken": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(typeof(JToken).GetMethod("Parse", new[] { typeof(string) }), Expression.Convert(parmExp, typeof(string))), 
			//					typeof(JToken)
			//				), parmExp).Compile();
			//		case "Newtonsoft.Json.Linq.JObject": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(typeof(JObject).GetMethod("Parse", new[] { typeof(string) }), Expression.Convert(parmExp, typeof(string))), 
			//					typeof(JObject)
			//				), parmExp).Compile();
			//		case "Newtonsoft.Json.Linq.JArray": return Expression.Lambda<Func<object, object>>(
			//				Expression.TypeAs(
			//					Expression.Call(typeof(JArray).GetMethod("Parse", new[] { typeof(string) }), Expression.Convert(parmExp, typeof(string))), 
			//					typeof(JArray)
			//				), parmExp).Compile();
			//		case "Npgsql.LegacyPostgis.PostgisGeometry": return Expression.Lambda<Func<object, object>>(parmExp, parmExp).Compile();
			//	}
			//	if (type != valueType) {
			//		if (type.FullName == "System.TimeSpan") return Expression.Lambda<Func<object, object>>(
			//			Expression.Convert(Expression.Call(
			//				MethodTimeSpanFromSeconds,
			//				Expression.Call(MethodDoubleParse, Expression.Call(MethodToString, parmExp))
			//			), typeof(object)), parmExp).Compile();
			//		return Expression.Lambda<Func<object, object>>(
			//			Expression.Call(MethodConvertChangeType, parmExp, Expression.Constant(type, typeof(Type)))
			//		, parmExp).Compile();
			//	}
			//	return Expression.Lambda<Func<object, object>>(parmExp, parmExp).Compile();
			//});
			//return func(value);
		}
		internal static object GetDataReaderValue22(Type type, object value) {
			if (value == null || value == DBNull.Value) return null;
			if (type.FullName == "System.Byte[]") return value;
			if (type.IsArray) {
				var elementType = type.GetElementType();
				var valueArr = value as Array;
				if (elementType == valueArr.GetType().GetElementType()) return value;
				var len = valueArr.GetLength(0);
				var ret = Array.CreateInstance(elementType, len);
				for (var a = 0; a < len; a++) {
					var item = valueArr.GetValue(a);
					ret.SetValue(GetDataReaderValue22(elementType, item), a);
				}
				return ret;
			}
			if (type.FullName.StartsWith("System.Nullable`1[")) type = type.GenericTypeArguments.First();
			if (type.IsEnum) return Enum.Parse(type, string.Concat(value), true);
			switch (type.FullName) {
				case "System.Guid":
					if (value.GetType() != type) return Guid.TryParse(string.Concat(value), out var tryguid) ? tryguid : Guid.Empty;
					return value;
				case "MygisPoint": return MygisPoint.Parse(string.Concat(value)) as MygisPoint;
				case "MygisLineString": return MygisLineString.Parse(string.Concat(value)) as MygisLineString;
				case "MygisPolygon": return MygisPolygon.Parse(string.Concat(value)) as MygisPolygon;
				case "MygisMultiPoint": return MygisMultiPoint.Parse(string.Concat(value)) as MygisMultiPoint;
				case "MygisMultiLineString": return MygisMultiLineString.Parse(string.Concat(value)) as MygisMultiLineString;
				case "MygisMultiPolygon": return MygisMultiPolygon.Parse(string.Concat(value)) as MygisMultiPolygon;
				case "Newtonsoft.Json.Linq.JToken": return JToken.Parse(string.Concat(value));
				case "Newtonsoft.Json.Linq.JObject": return JObject.Parse(string.Concat(value));
				case "Newtonsoft.Json.Linq.JArray": return JArray.Parse(string.Concat(value));
				case "Npgsql.LegacyPostgis.PostgisGeometry": return value;
			}
			if (type != value.GetType()) {
				if (type.FullName == "System.TimeSpan") return TimeSpan.FromSeconds(double.Parse(value.ToString()));
				return Convert.ChangeType(value, type);
			}
			return value;
		}
		internal static string GetCsName(string name) {
			name = Regex.Replace(name.TrimStart('@'), @"[^\w]", "_");
			return char.IsLetter(name, 0) ? name : string.Concat("_", name);
		}
	}
}