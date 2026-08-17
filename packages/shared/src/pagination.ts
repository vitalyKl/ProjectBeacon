export const PAGINATION_DEFAULT_LIMIT = 50;
export const PAGINATION_MAX_LIMIT = 100;

export type Page<T> = {
  items: T[];
  next_cursor: string | null;
};
